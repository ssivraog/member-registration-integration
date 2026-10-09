using Azure.Core;
using Azure.Identity;
using MemberRegistration.Core;
using MemberRegistration.Functions.Auth;
using MemberRegistration.Functions.Legacy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace MemberRegistration.Functions;

public static class ServiceRegistration
{
    /// <summary>Everything the functions need; Program.cs calls it, and the tests build it directly.</summary>
    public static IServiceCollection AddMemberRegistration(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<RegistrationTranslator>();

        // Inbound: Entra ID bearer tokens. Startup fails if misconfigured; Disabled is only allowed in Development.
        services.AddOptions<InboundAuthOptions>().BindConfiguration(InboundAuthOptions.Section).ValidateOnStart();
        services.AddSingleton<IValidateOptions<InboundAuthOptions>, InboundAuthOptionsValidator>();
        services.AddSingleton<IInboundAuthorizer>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<InboundAuthOptions>>();
            return options.Value.Mode == InboundAuthMode.Disabled
                ? new DisabledInboundAuthorizer()
                : ActivatorUtilities.CreateInstance<EntraIdInboundAuthorizer>(sp, EntraIdInboundAuthorizer.MetadataFor(options.Value));
        });

        // Outbound: the legacy API over https, authenticated on every attempt.
        services.AddOptions<LegacyApiOptions>().BindConfiguration(LegacyApiOptions.Section).ValidateOnStart();
        services.AddSingleton<IValidateOptions<LegacyApiOptions>, LegacyApiOptionsValidator>();
        services.AddSingleton<TokenCredential>(sp =>
        {
            var auth = sp.GetRequiredService<IOptions<LegacyApiOptions>>().Value.Auth;
            return new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ManagedIdentityClientId = string.IsNullOrWhiteSpace(auth.ManagedIdentityClientId) ? null : auth.ManagedIdentityClientId,
            });
        });

        var legacyClient = services.AddHttpClient<ILegacyMembershipClient, LegacyMembershipClient>((sp, http) =>
        {
            var options = sp.GetRequiredService<IOptions<LegacyApiOptions>>().Value;
            http.BaseAddress = new Uri(options.BaseUrl!.TrimEnd('/') + "/");
            http.Timeout = Timeout.InfiniteTimeSpan; // the resilience handler owns timeouts
        });

        // Handlers added first are outermost: resilience wraps auth, so every retry carries a valid credential.
        legacyClient.AddStandardResilienceHandler().Configure((resilience, sp) =>
        {
            var options = sp.GetRequiredService<IOptions<LegacyApiOptions>>().Value;
            resilience.AttemptTimeout.Timeout = options.AttemptTimeout;
            resilience.TotalRequestTimeout.Timeout = options.TotalTimeout;
            resilience.Retry.MaxRetryAttempts = 3;
            resilience.Retry.UseJitter = true;
            // The circuit breaker's sampling window must be at least twice the attempt timeout.
            var minimumSampling = options.AttemptTimeout * 2;
            if (resilience.CircuitBreaker.SamplingDuration < minimumSampling)
            {
                resilience.CircuitBreaker.SamplingDuration = minimumSampling;
            }
        });

        legacyClient.AddHttpMessageHandler(sp =>
        {
            var auth = sp.GetRequiredService<IOptions<LegacyApiOptions>>().Value.Auth;
            return auth.Mode switch
            {
                LegacyAuthMode.ManagedIdentity => new BearerTokenHandler(
                    sp.GetRequiredService<TokenCredential>(), auth.Scope!, sp.GetRequiredService<TimeProvider>()),
                LegacyAuthMode.ApiKey => new ApiKeyHandler(auth.ApiKeyHeader, auth.ApiKey!),
                _ => new PassThroughHandler(),
            };
        });

        return services;
    }

    /// <summary>LegacyAuthMode.None (Development only): sends the request unchanged.</summary>
    private sealed class PassThroughHandler : DelegatingHandler;
}
