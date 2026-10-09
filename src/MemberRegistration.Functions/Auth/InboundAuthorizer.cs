using MemberRegistration.Functions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace MemberRegistration.Functions.Auth;

/// <summary>Decides whether an inbound request may call an endpoint.</summary>
public interface IInboundAuthorizer
{
    /// <returns>Null when the caller is allowed; otherwise the 401/403 response to return unchanged.</returns>
    Task<IActionResult?> AuthorizeAsync(HttpRequest request, string requiredRole, CancellationToken cancellationToken);
}

/// <summary>Development only (enforced by <see cref="InboundAuthOptionsValidator"/>): lets every request through.</summary>
public sealed class DisabledInboundAuthorizer : IInboundAuthorizer
{
    public Task<IActionResult?> AuthorizeAsync(HttpRequest request, string requiredRole, CancellationToken cancellationToken) =>
        Task.FromResult<IActionResult?>(null);
}

/// <summary>
/// Validates the caller's Entra ID access token in code: signature against the tenant's published keys,
/// issuer, audience, lifetime and algorithm, then the endpoint's app role and, optionally, the caller's
/// application id. Responses never say which check failed; the reason is logged, the token never is.
/// </summary>
public sealed class EntraIdInboundAuthorizer : IInboundAuthorizer
{
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);

    private readonly InboundAuthOptions _options;
    private readonly IConfigurationManager<OpenIdConnectConfiguration> _metadata;
    private readonly ILogger<EntraIdInboundAuthorizer> _logger;
    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false };

    public EntraIdInboundAuthorizer(
        IOptions<InboundAuthOptions> options,
        IConfigurationManager<OpenIdConnectConfiguration> metadata,
        ILogger<EntraIdInboundAuthorizer> logger)
    {
        _options = options.Value;
        _metadata = metadata;
        _logger = logger;
    }

    /// <summary>The tenant's v2.0 OpenID metadata; signing keys are fetched from it and refreshed on rotation.</summary>
    public static IConfigurationManager<OpenIdConnectConfiguration> MetadataFor(InboundAuthOptions options) =>
        new ConfigurationManager<OpenIdConnectConfiguration>(
            $"{options.AuthorityHost.TrimEnd('/')}/{options.TenantId}/v2.0/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever { RequireHttps = true });

    public async Task<IActionResult?> AuthorizeAsync(HttpRequest request, string requiredRole, CancellationToken cancellationToken)
    {
        var token = BearerToken(request);
        if (token is null)
        {
            _logger.LogWarning("Inbound request rejected: no bearer token.");
            return Unauthorized();
        }

        var configuration = await _metadata.GetConfigurationAsync(cancellationToken);
        var result = await _handler.ValidateTokenAsync(token, Parameters(configuration));
        if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            // Keys may have rotated since they were cached: refresh once and retry.
            _metadata.RequestRefresh();
            result = await _handler.ValidateTokenAsync(token, Parameters(await _metadata.GetConfigurationAsync(cancellationToken)));
        }

        if (!result.IsValid)
        {
            _logger.LogWarning("Inbound request rejected: invalid token ({Reason}).", result.Exception?.GetType().Name ?? "unknown");
            return Unauthorized();
        }

        var callerAppId = Claim(result, "azp") ?? Claim(result, "appid");
        if (_options.AllowedCallerAppIds.Length > 0
            && (callerAppId is null || !_options.AllowedCallerAppIds.Contains(callerAppId, StringComparer.OrdinalIgnoreCase)))
        {
            _logger.LogWarning("Inbound request forbidden: caller app {CallerAppId} is not allowed.", callerAppId ?? "(none)");
            return Forbidden();
        }

        if (!HasRole(result, requiredRole))
        {
            _logger.LogWarning("Inbound request forbidden: caller app {CallerAppId} lacks role {Role}.", callerAppId ?? "(none)", requiredRole);
            return Forbidden();
        }

        _logger.LogInformation("Inbound request authorized for caller app {CallerAppId} with role {Role}.", callerAppId, requiredRole);
        return null;
    }

    private TokenValidationParameters Parameters(OpenIdConnectConfiguration configuration) => new()
    {
        ValidateIssuer = true,
        // v2.0 and v1.0 access tokens for the same tenant.
        ValidIssuers =
        [
            $"{_options.AuthorityHost.TrimEnd('/')}/{_options.TenantId}/v2.0",
            $"https://sts.windows.net/{_options.TenantId}/",
        ],
        ValidateAudience = true,
        ValidAudiences = Audiences(),
        ValidateLifetime = true,
        RequireExpirationTime = true,
        RequireSignedTokens = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKeys = configuration.SigningKeys,
        // Entra ID signs access tokens with RS256; anything else (including "none") is refused.
        ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        ClockSkew = ClockSkew,
    };

    // "api://x" and the bare client id both identify this API, depending on how the caller asked.
    private string[] Audiences() =>
        _options.Audience!.StartsWith("api://", StringComparison.OrdinalIgnoreCase)
            ? [_options.Audience, _options.Audience["api://".Length..]]
            : [_options.Audience];

    private static string? BearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = header[scheme.Length..].Trim();
        return token.Length == 0 ? null : token;
    }

    private static string? Claim(TokenValidationResult result, string type) =>
        result.Claims.TryGetValue(type, out var value) ? value?.ToString() : null;

    private static bool HasRole(TokenValidationResult result, string role) =>
        result.ClaimsIdentity.Claims.Any(c => c.Type == "roles" && c.Value == role);

    private static ContentResult Unauthorized()
    {
        var response = HttpSupport.Problem(StatusCodes.Status401Unauthorized, "A valid bearer token is required.");
        return new WithHeader(response, "WWW-Authenticate", "Bearer");
    }

    private static ContentResult Forbidden() =>
        HttpSupport.Problem(StatusCodes.Status403Forbidden, "The caller is not allowed to use this endpoint.");

    /// <summary>A problem response that also sets one response header when executed.</summary>
    public sealed class WithHeader : ContentResult
    {
        public WithHeader(ContentResult inner, string name, string value)
        {
            StatusCode = inner.StatusCode;
            ContentType = inner.ContentType;
            Content = inner.Content;
            HeaderName = name;
            HeaderValue = value;
        }

        public string HeaderName { get; }

        public string HeaderValue { get; }

        public override Task ExecuteResultAsync(ActionContext context)
        {
            context.HttpContext.Response.Headers[HeaderName] = HeaderValue;
            return base.ExecuteResultAsync(context);
        }
    }
}
