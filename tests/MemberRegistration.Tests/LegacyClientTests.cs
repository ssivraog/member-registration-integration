using System.Net;
using Azure.Core;
using MemberRegistration.Core;
using MemberRegistration.Functions.Legacy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static MemberRegistration.Tests.TestSupport;

namespace MemberRegistration.Tests;

/// <summary>Terminal handler: records what was sent and answers with a fixed status (or throws).</summary>
internal sealed class RecordingHandler(HttpStatusCode status = HttpStatusCode.Created, Exception? throws = null) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    public List<string> Bodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
        if (throws is not null)
        {
            throw throws;
        }

        return new HttpResponseMessage(status);
    }
}

internal sealed class FakeCredential(TimeProvider clock, TimeSpan lifetime) : TokenCredential
{
    public int Calls { get; private set; }

    public List<string[]> Scopes { get; } = [];

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        GetTokenAsync(requestContext, cancellationToken).AsTask().Result;

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        Calls++;
        Scopes.Add(requestContext.Scopes);
        return ValueTask.FromResult(new AccessToken($"token-{Calls}", clock.GetUtcNow() + lifetime));
    }
}

internal sealed class MutableClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

public class LegacyClientTests
{
    private const string Scope = "api://legacy-membership/.default";

    private static HttpClient Client(DelegatingHandler auth, RecordingHandler terminal)
    {
        auth.InnerHandler = terminal;
        return new HttpClient(auth) { BaseAddress = new Uri("https://legacy.example.com/api/") };
    }

    private static LegacyMembershipClient LegacyClient(HttpClient http) =>
        new(http, Options.Create(new LegacyApiOptions { BaseUrl = "https://legacy.example.com/api/", SubmitPath = "members" }),
            NullLogger<LegacyMembershipClient>.Instance);

    private static Core.Contracts.LegacyMemberRequest SampleRequest() =>
        Assert.IsType<TranslationResult.Success>(Translator().Translate(SampleRegistration)).Request;

    [Fact]
    public async Task Bearer_handler_sends_a_managed_identity_token_for_the_configured_scope()
    {
        var clock = new MutableClock(Now);
        var credential = new FakeCredential(clock, TimeSpan.FromHours(1));
        var terminal = new RecordingHandler();

        await Client(new BearerTokenHandler(credential, Scope, clock), terminal).GetAsync("ping");

        Assert.Equal("Bearer", terminal.Requests[0].Headers.Authorization!.Scheme);
        Assert.Equal("token-1", terminal.Requests[0].Headers.Authorization!.Parameter);
        Assert.Equal([Scope], credential.Scopes.Single());
    }

    [Fact]
    public async Task Bearer_handler_reuses_a_token_until_five_minutes_before_expiry()
    {
        var clock = new MutableClock(Now);
        var credential = new FakeCredential(clock, TimeSpan.FromHours(1));
        var terminal = new RecordingHandler();
        var http = Client(new BearerTokenHandler(credential, Scope, clock), terminal);

        await http.GetAsync("a");
        clock.Now += TimeSpan.FromMinutes(54);
        await http.GetAsync("b");
        clock.Now += TimeSpan.FromMinutes(2); // now inside the 5-minute renewal window
        await http.GetAsync("c");

        Assert.Equal(2, credential.Calls);
        Assert.Equal(["token-1", "token-1", "token-2"], terminal.Requests.Select(r => r.Headers.Authorization!.Parameter));
    }

    [Fact]
    public async Task Api_key_handler_sends_the_key_in_the_configured_header_and_replaces_any_existing_one()
    {
        var terminal = new RecordingHandler();
        var request = new HttpRequestMessage(HttpMethod.Get, "ping");
        request.Headers.Add("X-Legacy-Key", "spoofed");

        await Client(new ApiKeyHandler("X-Legacy-Key", "secret-from-key-vault"), terminal).SendAsync(request);

        Assert.Equal(["secret-from-key-vault"], terminal.Requests[0].Headers.GetValues("X-Legacy-Key"));
        Assert.Null(terminal.Requests[0].Headers.Authorization);
    }

    [Fact]
    public async Task Submit_posts_the_legacy_payload_with_idempotency_and_correlation_headers()
    {
        var terminal = new RecordingHandler();
        var client = LegacyClient(Client(new ApiKeyHandler("X-Api-Key", "k"), terminal));

        var result = await client.SubmitAsync(SampleRequest(), "crm-record-42", "corr-1", CancellationToken.None);

        Assert.Equal(new SubmissionResult(SubmissionOutcome.Accepted, 201), result);
        var sent = terminal.Requests.Single();
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("https://legacy.example.com/api/members", sent.RequestUri!.ToString());
        Assert.Equal("crm-record-42", sent.Headers.GetValues(LegacyMembershipClient.IdempotencyKeyHeader).Single());
        Assert.Equal("corr-1", sent.Headers.GetValues(LegacyMembershipClient.CorrelationIdHeader).Single());
        Assert.Equal(Minified(ExpectedLegacyPayload), terminal.Bodies.Single());
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, SubmissionOutcome.Accepted)]
    [InlineData(HttpStatusCode.Accepted, SubmissionOutcome.Accepted)]
    [InlineData(HttpStatusCode.Conflict, SubmissionOutcome.Duplicate)]
    [InlineData(HttpStatusCode.Unauthorized, SubmissionOutcome.AuthFailed)]
    [InlineData(HttpStatusCode.Forbidden, SubmissionOutcome.AuthFailed)]
    [InlineData(HttpStatusCode.BadRequest, SubmissionOutcome.Rejected)]
    [InlineData(HttpStatusCode.UnprocessableEntity, SubmissionOutcome.Rejected)]
    [InlineData(HttpStatusCode.RequestTimeout, SubmissionOutcome.Unavailable)]
    [InlineData(HttpStatusCode.TooManyRequests, SubmissionOutcome.Unavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, SubmissionOutcome.Unavailable)]
    public async Task Legacy_status_is_classified(HttpStatusCode status, SubmissionOutcome expected)
    {
        var client = LegacyClient(Client(new ApiKeyHandler("X-Api-Key", "k"), new RecordingHandler(status)));

        var result = await client.SubmitAsync(SampleRequest(), "key", "corr", CancellationToken.None);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal((int)status, result.LegacyStatus);
    }

    [Fact]
    public async Task Network_failure_is_unavailable()
    {
        var terminal = new RecordingHandler(throws: new HttpRequestException("connection refused"));
        var client = LegacyClient(Client(new ApiKeyHandler("X-Api-Key", "k"), terminal));

        var result = await client.SubmitAsync(SampleRequest(), "key", "corr", CancellationToken.None);

        Assert.Equal(new SubmissionResult(SubmissionOutcome.Unavailable, null), result);
    }
}
