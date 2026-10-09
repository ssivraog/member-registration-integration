using MemberRegistration.Functions;
using MemberRegistration.Functions.Auth;
using MemberRegistration.Functions.Legacy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MemberRegistration.Tests;

/// <summary>The real container, as Program.cs builds it, from configuration only.</summary>
public class ServiceRegistrationTests
{
    private static readonly Dictionary<string, string?> ProductionSettings = new()
    {
        ["Auth:Inbound:TenantId"] = FakeTenant.TenantId,
        ["Auth:Inbound:Audience"] = FakeTenant.Audience,
        ["LegacyApi:BaseUrl"] = "https://legacy.example.com/api",
        ["LegacyApi:Auth:Mode"] = "ManagedIdentity",
        ["LegacyApi:Auth:Scope"] = "api://legacy/.default",
    };

    private static ServiceProvider Build(Dictionary<string, string?> settings, string environment = "Production")
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddSingleton<IHostEnvironment>(new FakeEnvironment(environment));
        services.AddLogging();
        services.AddMemberRegistration();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    /// <summary>What ValidateOnStart does when the host starts.</summary>
    private static void ValidateStartup(IServiceProvider services) =>
        services.GetRequiredService<IStartupValidator>().Validate();

    /// <summary>Startup must refuse; several failing sections arrive wrapped in an AggregateException.</summary>
    private static string AssertStartupRefused(IServiceProvider services)
    {
        var error = Assert.ThrowsAny<Exception>(() => ValidateStartup(services));
        var failures = error is AggregateException aggregate ? aggregate.Flatten().InnerExceptions : [error];
        Assert.All(failures, f => Assert.IsType<OptionsValidationException>(f));
        return string.Join(" | ", failures.Select(f => f.Message));
    }

    [Fact]
    public void Production_settings_resolve_the_entra_authorizer_and_the_legacy_client()
    {
        using var services = Build(ProductionSettings);

        ValidateStartup(services);
        Assert.IsType<EntraIdInboundAuthorizer>(services.GetRequiredService<IInboundAuthorizer>());
        Assert.IsType<LegacyMembershipClient>(services.GetRequiredService<ILegacyMembershipClient>());
    }

    [Fact]
    public void Api_key_mode_resolves_the_legacy_client()
    {
        var settings = new Dictionary<string, string?>(ProductionSettings)
        {
            ["LegacyApi:Auth:Mode"] = "ApiKey",
            ["LegacyApi:Auth:ApiKey"] = "from-key-vault-reference",
        };
        using var services = Build(settings);

        ValidateStartup(services);
        Assert.IsType<LegacyMembershipClient>(services.GetRequiredService<ILegacyMembershipClient>());
    }

    [Fact]
    public void Missing_inbound_settings_fail_at_startup()
    {
        var settings = new Dictionary<string, string?>(ProductionSettings) { ["Auth:Inbound:TenantId"] = null };
        using var services = Build(settings);

        AssertStartupRefused(services);
    }

    [Fact]
    public void Http_legacy_url_fails_at_startup_in_production()
    {
        var settings = new Dictionary<string, string?>(ProductionSettings) { ["LegacyApi:BaseUrl"] = "http://legacy.example.com" };
        using var services = Build(settings);

        AssertStartupRefused(services);
    }

    [Fact]
    public void Local_settings_start_in_development_with_auth_disabled()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Auth:Inbound:Mode"] = "Disabled",
            ["LegacyApi:BaseUrl"] = "http://localhost:5080",
            ["LegacyApi:Auth:Mode"] = "None",
        };
        using var services = Build(settings, "Development");

        ValidateStartup(services);
        Assert.IsType<DisabledInboundAuthorizer>(services.GetRequiredService<IInboundAuthorizer>());
    }

    [Fact]
    public void The_same_local_settings_refuse_to_start_in_production()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Auth:Inbound:Mode"] = "Disabled",
            ["LegacyApi:BaseUrl"] = "http://localhost:5080",
            ["LegacyApi:Auth:Mode"] = "None",
        };
        using var services = Build(settings);

        var message = AssertStartupRefused(services);
        Assert.Contains("Development", message);
    }
}
