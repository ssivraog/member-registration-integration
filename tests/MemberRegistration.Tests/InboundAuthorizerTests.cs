using MemberRegistration.Functions.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using static MemberRegistration.Tests.FakeTenant;

namespace MemberRegistration.Tests;

public class InboundAuthorizerTests
{
    private static Task<IActionResult?> AuthorizeAsync(string? authorization, string role = AppRoles.Translate,
        InboundAuthOptions? options = null) =>
        Authorizer(options).AuthorizeAsync(Request(authorization), role, CancellationToken.None);

    private static void AssertUnauthorized(IActionResult? result)
    {
        var response = Assert.IsType<EntraIdInboundAuthorizer.WithHeader>(result);
        Assert.Equal(401, response.StatusCode);
        Assert.Equal("application/problem+json", response.ContentType);
        Assert.Equal("WWW-Authenticate", response.HeaderName);
        Assert.Equal("Bearer", response.HeaderValue);
    }

    private static void AssertForbidden(IActionResult? result) =>
        Assert.Equal(403, Assert.IsType<ContentResult>(result).StatusCode);

    [Fact]
    public async Task Valid_token_with_the_role_is_allowed()
    {
        Assert.Null(await AuthorizeAsync($"Bearer {Token()}"));
    }

    [Fact]
    public async Task Bearer_scheme_is_case_insensitive()
    {
        Assert.Null(await AuthorizeAsync($"bearer {Token()}"));
    }

    [Fact]
    public async Task Bare_client_id_audience_is_accepted_for_an_api_uri()
    {
        Assert.Null(await AuthorizeAsync($"Bearer {Token(audience: "member-registration")}"));
    }

    [Fact]
    public async Task V1_issuer_for_the_same_tenant_is_accepted()
    {
        Assert.Null(await AuthorizeAsync($"Bearer {Token(issuer: $"https://sts.windows.net/{TenantId}/")}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer")]
    [InlineData("Bearer   ")]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("Bearer not-a-jwt")]
    public async Task Missing_or_malformed_credentials_are_401(string? authorization)
    {
        AssertUnauthorized(await AuthorizeAsync(authorization));
    }

    [Fact]
    public async Task Expired_token_is_401()
    {
        // Past the 2-minute clock skew.
        AssertUnauthorized(await AuthorizeAsync($"Bearer {Token(expires: DateTime.UtcNow.AddMinutes(-5))}"));
    }

    [Fact]
    public async Task Token_for_another_api_is_401()
    {
        AssertUnauthorized(await AuthorizeAsync($"Bearer {Token(audience: "api://some-other-api")}"));
    }

    [Fact]
    public async Task Token_from_another_tenant_is_401()
    {
        AssertUnauthorized(await AuthorizeAsync(
            $"Bearer {Token(issuer: "https://login.microsoftonline.com/99999999-9999-9999-9999-999999999999/v2.0")}"));
    }

    [Fact]
    public async Task Token_signed_with_an_unknown_key_is_401()
    {
        AssertUnauthorized(await AuthorizeAsync($"Bearer {Token(key: NewKey("attacker-key"))}"));
    }

    [Fact]
    public async Task Unsigned_alg_none_token_is_401()
    {
        AssertUnauthorized(await AuthorizeAsync($"Bearer {UnsignedToken()}"));
    }

    [Fact]
    public async Task Hmac_signed_token_is_401_even_with_valid_claims()
    {
        var hmacKey = new SymmetricSecurityKey(new byte[32]) { KeyId = "tenant-key" };

        AssertUnauthorized(await AuthorizeAsync($"Bearer {Token(key: hmacKey, algorithm: SecurityAlgorithms.HmacSha256)}"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(AppRoles.Translate)]
    [InlineData("registrations.submit")] // role values are case-sensitive
    public async Task Valid_token_without_the_required_role_is_403(string roles)
    {
        var granted = roles.Split(',', StringSplitOptions.RemoveEmptyEntries);

        AssertForbidden(await AuthorizeAsync($"Bearer {Token(roles: granted)}", AppRoles.Submit));
    }

    [Fact]
    public async Task Caller_on_the_allow_list_is_allowed()
    {
        Assert.Null(await AuthorizeAsync($"Bearer {Token()}", options: Options(CrmAppId)));
    }

    [Theory]
    [InlineData("bbbbbbbb-0000-0000-0000-000000000002")]
    [InlineData(null)]
    public async Task Caller_off_the_allow_list_is_403(string? callerAppId)
    {
        AssertForbidden(await AuthorizeAsync($"Bearer {Token(callerAppId: callerAppId)}", options: Options(CrmAppId)));
    }

    [Fact]
    public async Task Disabled_authorizer_allows_everything()
    {
        Assert.Null(await new DisabledInboundAuthorizer().AuthorizeAsync(Request(null), AppRoles.Submit, CancellationToken.None));
    }
}
