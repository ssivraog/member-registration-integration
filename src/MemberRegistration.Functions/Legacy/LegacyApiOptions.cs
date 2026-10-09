using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MemberRegistration.Functions.Legacy;

public enum LegacyAuthMode
{
    /// <summary>
    /// Entra ID access token from the Function's managed identity (DefaultAzureCredential). No secret
    /// exists anywhere; preferred whenever the legacy API, or APIM in front of it, accepts tokens.
    /// </summary>
    ManagedIdentity,

    /// <summary>
    /// Static API key in a header, for legacy APIs that can't take tokens. The key must come from a Key
    /// Vault reference in app settings, never from code or local.settings.json.
    /// </summary>
    ApiKey,

    /// <summary>No credential. Only allowed in the Development environment (a local stub).</summary>
    None,
}

/// <summary>Configuration section <c>LegacyApi</c>.</summary>
public sealed class LegacyApiOptions
{
    public const string Section = "LegacyApi";

    public string? BaseUrl { get; set; }

    /// <summary>Path of the create-member endpoint, relative to <see cref="BaseUrl"/>.</summary>
    public string SubmitPath { get; set; } = "members";

    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public LegacyAuthOptions Auth { get; set; } = new();
}

public sealed class LegacyAuthOptions
{
    public LegacyAuthMode Mode { get; set; } = LegacyAuthMode.ManagedIdentity;

    /// <summary>ManagedIdentity: the legacy API's scope, e.g. <c>api://legacy-membership/.default</c>.</summary>
    public string? Scope { get; set; }

    /// <summary>ManagedIdentity: client id of a user-assigned identity; empty uses the system-assigned one.</summary>
    public string? ManagedIdentityClientId { get; set; }

    /// <summary>ApiKey: header the key is sent in.</summary>
    public string ApiKeyHeader { get; set; } = "X-Api-Key";

    /// <summary>ApiKey: the key itself - set as <c>@Microsoft.KeyVault(SecretUri=...)</c> in app settings.</summary>
    public string? ApiKey { get; set; }
}

/// <summary>Fails startup on missing or unsafe outbound settings.</summary>
public sealed class LegacyApiOptionsValidator(IHostEnvironment environment) : IValidateOptions<LegacyApiOptions>
{
    public ValidateOptionsResult Validate(string? name, LegacyApiOptions options)
    {
        var failures = new List<string>();
        var development = environment.IsDevelopment();

        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUrl))
        {
            failures.Add($"{LegacyApiOptions.Section}:BaseUrl must be an absolute URL.");
        }
        else if (baseUrl.Scheme != Uri.UriSchemeHttps && !(development && baseUrl.IsLoopback))
        {
            // A token or API key must never travel in clear text.
            failures.Add($"{LegacyApiOptions.Section}:BaseUrl must use https (http is only allowed for localhost in Development).");
        }

        if (options.AttemptTimeout <= TimeSpan.Zero || options.TotalTimeout < options.AttemptTimeout)
        {
            failures.Add($"{LegacyApiOptions.Section}: AttemptTimeout must be positive and no longer than TotalTimeout.");
        }

        var auth = options.Auth;
        switch (auth.Mode)
        {
            case LegacyAuthMode.ManagedIdentity when string.IsNullOrWhiteSpace(auth.Scope):
                failures.Add($"{LegacyApiOptions.Section}:Auth:Scope is required for ManagedIdentity.");
                break;
            case LegacyAuthMode.ApiKey when string.IsNullOrWhiteSpace(auth.ApiKey):
                failures.Add($"{LegacyApiOptions.Section}:Auth:ApiKey is required for ApiKey (use a Key Vault reference).");
                break;
            case LegacyAuthMode.ApiKey when string.IsNullOrWhiteSpace(auth.ApiKeyHeader):
                failures.Add($"{LegacyApiOptions.Section}:Auth:ApiKeyHeader is required for ApiKey.");
                break;
            case LegacyAuthMode.None when !development:
                failures.Add($"{LegacyApiOptions.Section}:Auth:Mode=None is only allowed in the Development environment.");
                break;
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
