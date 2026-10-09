using MemberRegistration.Functions.Auth;
using MemberRegistration.Functions.Legacy;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace MemberRegistration.Tests;

internal sealed class FakeEnvironment(string name) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = name;

    public string ApplicationName { get; set; } = "MemberRegistration.Functions";

    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

public class AuthOptionsValidatorTests
{
    private static readonly FakeEnvironment Production = new(Environments.Production);
    private static readonly FakeEnvironment Development = new(Environments.Development);

    private static bool InboundValid(InboundAuthOptions options, IHostEnvironment environment) =>
        new InboundAuthOptionsValidator(environment).Validate(null, options).Succeeded;

    private static bool LegacyValid(LegacyApiOptions options, IHostEnvironment environment) =>
        new LegacyApiOptionsValidator(environment).Validate(null, options).Succeeded;

    private static LegacyApiOptions Legacy(string baseUrl = "https://legacy.example.com", Action<LegacyAuthOptions>? auth = null)
    {
        var options = new LegacyApiOptions { BaseUrl = baseUrl, Auth = { Mode = LegacyAuthMode.ManagedIdentity, Scope = "api://legacy/.default" } };
        auth?.Invoke(options.Auth);
        return options;
    }

    [Fact]
    public void Complete_inbound_settings_are_valid() =>
        Assert.True(InboundValid(FakeTenant.Options(), Production));

    [Fact]
    public void Inbound_defaults_to_entra_id_and_fails_closed_without_settings() =>
        Assert.False(InboundValid(new InboundAuthOptions(), Production));

    [Theory]
    [InlineData("not-a-guid", "api://member-registration")]
    [InlineData(FakeTenant.TenantId, "")]
    [InlineData(FakeTenant.TenantId, null)]
    public void Incomplete_inbound_settings_are_invalid(string tenantId, string? audience) =>
        Assert.False(InboundValid(new InboundAuthOptions { TenantId = tenantId, Audience = audience }, Production));

    [Fact]
    public void Plain_http_authority_is_invalid()
    {
        var options = FakeTenant.Options();
        options.AuthorityHost = "http://login.microsoftonline.com";

        Assert.False(InboundValid(options, Production));
    }

    [Fact]
    public void Disabled_inbound_auth_is_rejected_outside_development() =>
        Assert.False(InboundValid(new InboundAuthOptions { Mode = InboundAuthMode.Disabled }, Production));

    [Fact]
    public void Disabled_inbound_auth_is_allowed_in_development() =>
        Assert.True(InboundValid(new InboundAuthOptions { Mode = InboundAuthMode.Disabled }, Development));

    [Fact]
    public void Managed_identity_with_https_and_scope_is_valid() =>
        Assert.True(LegacyValid(Legacy(), Production));

    [Fact]
    public void Managed_identity_without_scope_is_invalid() =>
        Assert.False(LegacyValid(Legacy(auth: a => a.Scope = null), Production));

    [Fact]
    public void Api_key_mode_requires_the_key() =>
        Assert.False(LegacyValid(Legacy(auth: a => { a.Mode = LegacyAuthMode.ApiKey; a.ApiKey = ""; }), Production));

    [Fact]
    public void Api_key_mode_with_a_key_is_valid() =>
        Assert.True(LegacyValid(Legacy(auth: a => { a.Mode = LegacyAuthMode.ApiKey; a.ApiKey = "from-key-vault"; }), Production));

    [Theory]
    [InlineData("http://legacy.example.com")]
    [InlineData("http://localhost:5080")]
    [InlineData("not a url")]
    public void Non_https_base_url_is_invalid_in_production(string baseUrl) =>
        Assert.False(LegacyValid(Legacy(baseUrl), Production));

    [Fact]
    public void Http_localhost_stub_is_allowed_in_development() =>
        Assert.True(LegacyValid(Legacy("http://localhost:5080", a => a.Mode = LegacyAuthMode.None), Development));

    [Fact]
    public void Http_remote_host_is_invalid_even_in_development() =>
        Assert.False(LegacyValid(Legacy("http://legacy.example.com"), Development));

    [Fact]
    public void No_auth_is_rejected_outside_development() =>
        Assert.False(LegacyValid(Legacy(auth: a => a.Mode = LegacyAuthMode.None), Production));

    [Fact]
    public void Attempt_timeout_longer_than_total_is_invalid()
    {
        var options = Legacy();
        options.AttemptTimeout = TimeSpan.FromSeconds(40);

        Assert.False(LegacyValid(options, Production));
    }
}
