using System.Text;
using System.Text.Json.Nodes;
using MemberRegistration.Core.Contracts;
using MemberRegistration.Functions;
using MemberRegistration.Functions.Auth;
using MemberRegistration.Functions.Legacy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using static MemberRegistration.Tests.TestSupport;

namespace MemberRegistration.Tests;

internal sealed class FakeLegacyClient(SubmissionOutcome outcome) : ILegacyMembershipClient
{
    public List<(LegacyMemberRequest Request, string IdempotencyKey, string CorrelationId)> Calls { get; } = [];

    public Task<SubmissionResult> SubmitAsync(LegacyMemberRequest request, string idempotencyKey, string correlationId,
        CancellationToken cancellationToken)
    {
        Calls.Add((request, idempotencyKey, correlationId));
        return Task.FromResult(new SubmissionResult(outcome, null));
    }
}

public class SubmitRegistrationFunctionTests
{
    private static async Task<ContentResult> PostAsync(
        string body,
        FakeLegacyClient legacy,
        string? idempotencyKey = "crm-record-42",
        string? correlationId = null,
        StubAuthorizer? authorizer = null)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        if (idempotencyKey is not null)
        {
            context.Request.Headers[LegacyMembershipClient.IdempotencyKeyHeader] = idempotencyKey;
        }

        if (correlationId is not null)
        {
            context.Request.Headers[LegacyMembershipClient.CorrelationIdHeader] = correlationId;
        }

        var function = new SubmitRegistrationFunction(Translator(), authorizer ?? StubAuthorizer.Allow(), legacy,
            NullLogger<SubmitRegistrationFunction>.Instance);
        return Assert.IsType<ContentResult>(await function.Run(context.Request, CancellationToken.None), exactMatch: false);
    }

    [Fact]
    public async Task Valid_registration_is_submitted_and_returns_202()
    {
        var legacy = new FakeLegacyClient(SubmissionOutcome.Accepted);

        var result = await PostAsync(SampleRegistration, legacy, correlationId: "corr-7");

        Assert.Equal(202, result.StatusCode);
        var call = Assert.Single(legacy.Calls);
        Assert.Equal(Minified(ExpectedLegacyPayload), LegacyJson.Serialize(call.Request));
        Assert.Equal("crm-record-42", call.IdempotencyKey);
        Assert.Equal("corr-7", call.CorrelationId);
        var response = JsonNode.Parse(result.Content!)!;
        Assert.Equal("submitted", response["status"]!.GetValue<string>());
        Assert.Equal("corr-7", response["correlationId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Requires_the_submit_role()
    {
        var authorizer = StubAuthorizer.Allow();

        await PostAsync(SampleRegistration, new FakeLegacyClient(SubmissionOutcome.Accepted), authorizer: authorizer);

        Assert.Equal(AppRoles.Submit, authorizer.RequestedRole);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task Unauthorized_caller_never_reaches_the_legacy_api(int status)
    {
        var legacy = new FakeLegacyClient(SubmissionOutcome.Accepted);

        var result = await PostAsync(SampleRegistration, legacy, authorizer: StubAuthorizer.Deny(status));

        Assert.Equal(status, result.StatusCode);
        Assert.Empty(legacy.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("quote\"injection")]
    public async Task Missing_or_unsafe_idempotency_key_is_400(string? key)
    {
        var legacy = new FakeLegacyClient(SubmissionOutcome.Accepted);

        var result = await PostAsync(SampleRegistration, legacy, idempotencyKey: key);

        Assert.Equal(400, result.StatusCode);
        Assert.Empty(legacy.Calls);
    }

    [Fact]
    public async Task Unsafe_correlation_id_is_replaced_not_forwarded()
    {
        var legacy = new FakeLegacyClient(SubmissionOutcome.Accepted);

        await PostAsync(SampleRegistration, legacy, correlationId: "evil\r\nX-Injected: 1");

        Assert.Matches("^[0-9a-f]{32}$", legacy.Calls.Single().CorrelationId);
    }

    [Fact]
    public async Task Invalid_registration_is_422_and_never_sent()
    {
        var legacy = new FakeLegacyClient(SubmissionOutcome.Accepted);

        var result = await PostAsync(Registration(r => r["email"] = "nope"), legacy);

        Assert.Equal(422, result.StatusCode);
        Assert.Empty(legacy.Calls);
    }

    [Theory]
    [InlineData(SubmissionOutcome.Duplicate, 409)]
    [InlineData(SubmissionOutcome.Rejected, 502)]
    [InlineData(SubmissionOutcome.AuthFailed, 502)]
    [InlineData(SubmissionOutcome.Unavailable, 503)]
    public async Task Legacy_outcome_maps_to_status(SubmissionOutcome outcome, int expected)
    {
        var result = await PostAsync(SampleRegistration, new FakeLegacyClient(outcome));

        Assert.Equal(expected, result.StatusCode);
    }

    [Fact]
    public async Task Legacy_auth_failure_does_not_reveal_credential_details()
    {
        var result = await PostAsync(SampleRegistration, new FakeLegacyClient(SubmissionOutcome.AuthFailed));

        Assert.DoesNotContain("401", result.Content);
        Assert.DoesNotContain("token", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("key", result.Content, StringComparison.OrdinalIgnoreCase);
    }
}
