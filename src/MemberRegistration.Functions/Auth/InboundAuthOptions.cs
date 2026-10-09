using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MemberRegistration.Functions.Auth;

public enum InboundAuthMode
{
    /// <summary>Every request must carry a valid Entra ID access token with the endpoint's app role.</summary>
    EntraId,

    /// <summary>No token check. Only allowed when the host environment is Development.</summary>
    Disabled,
}

/// <summary>Configuration section <c>Auth:Inbound</c>.</summary>
public sealed class InboundAuthOptions
{
    public const string Section = "Auth:Inbound";

    public InboundAuthMode Mode { get; set; } = InboundAuthMode.EntraId;

    /// <summary>Entra ID tenant (directory) id the callers' tokens are issued by.</summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// This API's Application ID URI or client id (e.g. <c>api://member-registration</c>). Tokens for any
    /// other audience are rejected, so a token minted for another API can't be replayed here.
    /// </summary>
    public string? Audience { get; set; }

    /// <summary>
    /// Optional allow-list of caller application (client) ids (<c>azp</c>/<c>appid</c>). Empty means any
    /// caller holding the app role; set it to pin the endpoint to the CRM's identity.
    /// </summary>
    public string[] AllowedCallerAppIds { get; set; } = [];

    /// <summary>Entra ID authority host; only changed for sovereign clouds.</summary>
    public string AuthorityHost { get; set; } = "https://login.microsoftonline.com";
}

/// <summary>Fails startup on missing or unsafe inbound auth settings: the endpoints never run unprotected by accident.</summary>
public sealed class InboundAuthOptionsValidator(IHostEnvironment environment) : IValidateOptions<InboundAuthOptions>
{
    public ValidateOptionsResult Validate(string? name, InboundAuthOptions options)
    {
        if (options.Mode == InboundAuthMode.Disabled)
        {
            return environment.IsDevelopment()
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail($"{InboundAuthOptions.Section}:Mode=Disabled is only allowed in the Development environment.");
        }

        var failures = new List<string>();
        if (!Guid.TryParse(options.TenantId, out _))
        {
            failures.Add($"{InboundAuthOptions.Section}:TenantId must be the Entra ID tenant GUID.");
        }

        if (string.IsNullOrWhiteSpace(options.Audience))
        {
            failures.Add($"{InboundAuthOptions.Section}:Audience is required.");
        }

        if (!Uri.TryCreate(options.AuthorityHost, UriKind.Absolute, out var authority) || authority.Scheme != Uri.UriSchemeHttps)
        {
            failures.Add($"{InboundAuthOptions.Section}:AuthorityHost must be an https URL.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
