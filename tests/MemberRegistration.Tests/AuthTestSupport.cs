using System.Security.Cryptography;
using System.Text;
using MemberRegistration.Functions.Auth;
using MemberRegistration.Functions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace MemberRegistration.Tests;

/// <summary>An authorizer double for the function tests: allows, or returns a fixed status, and records the role asked for.</summary>
internal sealed class StubAuthorizer(int? denyStatus) : IInboundAuthorizer
{
    public string? RequestedRole { get; private set; }

    public static StubAuthorizer Allow() => new(null);

    public static StubAuthorizer Deny(int status) => new(status);

    public Task<IActionResult?> AuthorizeAsync(HttpRequest request, string requiredRole, CancellationToken cancellationToken)
    {
        RequestedRole = requiredRole;
        return Task.FromResult<IActionResult?>(denyStatus is { } status ? HttpSupport.Problem(status, "denied") : null);
    }
}

/// <summary>A fake Entra ID tenant: its signing key, and tokens shaped like the ones it issues.</summary>
internal static class FakeTenant
{
    public const string TenantId = "11111111-2222-3333-4444-555555555555";
    public const string Audience = "api://member-registration";
    public const string CrmAppId = "aaaaaaaa-0000-0000-0000-000000000001";
    public const string Issuer = $"https://login.microsoftonline.com/{TenantId}/v2.0";

    public static readonly RsaSecurityKey SigningKey = NewKey("tenant-key");

    public static RsaSecurityKey NewKey(string keyId) => new(RSA.Create(2048)) { KeyId = keyId };

    public static InboundAuthOptions Options(params string[] allowedCallers) => new()
    {
        Mode = InboundAuthMode.EntraId,
        TenantId = TenantId,
        Audience = Audience,
        AllowedCallerAppIds = allowedCallers,
    };

    public static EntraIdInboundAuthorizer Authorizer(InboundAuthOptions? options = null)
    {
        var metadata = new OpenIdConnectConfiguration();
        metadata.SigningKeys.Add(SigningKey);
        return new EntraIdInboundAuthorizer(
            Microsoft.Extensions.Options.Options.Create(options ?? Options()),
            new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata),
            NullLogger<EntraIdInboundAuthorizer>.Instance);
    }

    public static string Token(
        string[]? roles = null,
        string audience = Audience,
        string issuer = Issuer,
        string? callerAppId = CrmAppId,
        DateTime? expires = null,
        SecurityKey? key = null,
        string algorithm = SecurityAlgorithms.RsaSha256)
    {
        var now = DateTime.UtcNow;
        var expiry = expires ?? now.AddMinutes(30);
        var claims = new Dictionary<string, object> { ["roles"] = roles ?? [AppRoles.Translate] };
        if (callerAppId is not null)
        {
            claims["azp"] = callerAppId;
        }

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = expiry.AddHours(-1),
            NotBefore = expiry.AddHours(-1),
            Expires = expiry,
            Claims = claims,
            SigningCredentials = new SigningCredentials(key ?? SigningKey, algorithm),
        });
    }

    /// <summary>A structurally valid but unsigned token ("alg": "none").</summary>
    public static string UnsignedToken()
    {
        static string Encode(string json) => Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(json));
        var expires = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
        return $"{Encode("""{"alg":"none","typ":"JWT"}""")}." +
               $"{Encode($$"""{"iss":"{{Issuer}}","aud":"{{Audience}}","exp":{{expires}},"roles":["{{AppRoles.Translate}}"],"azp":"{{CrmAppId}}"}""")}.";
    }

    public static HttpRequest Request(string? authorization)
    {
        var context = new DefaultHttpContext();
        if (authorization is not null)
        {
            context.Request.Headers.Authorization = authorization;
        }

        return context.Request;
    }
}
