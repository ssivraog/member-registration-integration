using System.Text;
using System.Text.Json.Nodes;
using MemberRegistration.Core.Validation;
using MemberRegistration.Functions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using static MemberRegistration.Tests.TestSupport;

namespace MemberRegistration.Tests;

public class TranslateRegistrationFunctionTests
{
    private static async Task<ContentResult> PostAsync(string body, string? contentType = "application/json",
        bool sendContentLength = true)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.ContentType = contentType;
        context.Request.Body = new MemoryStream(bytes);
        if (sendContentLength)
        {
            context.Request.ContentLength = bytes.Length;
        }

        var function = new TranslateRegistrationFunction(Translator(), NullLogger<TranslateRegistrationFunction>.Instance);
        var result = await function.Run(context.Request, CancellationToken.None);
        return Assert.IsType<ContentResult>(result);
    }

    [Fact]
    public async Task Valid_registration_returns_200_with_the_legacy_payload()
    {
        var result = await PostAsync(SampleRegistration);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("application/json", result.ContentType);
        Assert.Equal(Minified(ExpectedLegacyPayload), result.Content);
    }

    [Theory]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("APPLICATION/JSON")]
    [InlineData("application/vnd.crm.registration+json")]
    public async Task Json_content_type_variants_are_accepted(string contentType)
    {
        var result = await PostAsync(SampleRegistration, contentType);

        Assert.Equal(200, result.StatusCode);
    }

    [Fact]
    public async Task Validation_failure_returns_422_problem_details_with_every_error()
    {
        var body = Registration(r =>
        {
            r["email"] = "not-an-email";
            r["membershipType"] = "Gold";
        });

        var result = await PostAsync(body);

        Assert.Equal(422, result.StatusCode);
        Assert.Equal("application/problem+json", result.ContentType);
        var problem = JsonNode.Parse(result.Content!)!;
        Assert.Equal("Registration failed validation.", problem["title"]!.GetValue<string>());
        Assert.Equal(422, problem["status"]!.GetValue<int>());
        var errors = problem["errors"]!.AsArray();
        Assert.Equal(2, errors.Count);
        Assert.Equal("email", errors[0]!["field"]!.GetValue<string>());
        Assert.Equal(ErrorCodes.InvalidEmail, errors[0]!["code"]!.GetValue<string>());
        Assert.Equal("email is not a valid email address.", errors[0]!["message"]!.GetValue<string>());
        Assert.Equal("membershipType", errors[1]!["field"]!.GetValue<string>());
        Assert.Equal(ErrorCodes.InvalidMembershipType, errors[1]!["code"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("", ErrorCodes.BodyEmpty)]
    [InlineData("{not json", ErrorCodes.BodyMalformed)]
    [InlineData("[1, 2]", ErrorCodes.BodyNotObject)]
    public async Task Unreadable_body_returns_400_problem_details(string body, string expectedCode)
    {
        var result = await PostAsync(body);

        Assert.Equal(400, result.StatusCode);
        Assert.Equal("application/problem+json", result.ContentType);
        var error = JsonNode.Parse(result.Content!)!["errors"]!.AsArray().Single()!;
        Assert.Equal("body", error["field"]!.GetValue<string>());
        Assert.Equal(expectedCode, error["code"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("not a media type")]
    public async Task Non_json_content_type_returns_415(string? contentType)
    {
        var result = await PostAsync(SampleRegistration, contentType);

        Assert.Equal(415, result.StatusCode);
        Assert.Equal("application/problem+json", result.ContentType);
        Assert.Null(JsonNode.Parse(result.Content!)!["errors"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)] // chunked upload: no Content-Length, so the limit is enforced while reading
    public async Task Oversized_body_returns_413(bool sendContentLength)
    {
        var body = Registration(r => r["padding"] = new string('x', TranslateRegistrationFunction.MaxBodyBytes));

        var result = await PostAsync(body, sendContentLength: sendContentLength);

        Assert.Equal(413, result.StatusCode);
    }
}
