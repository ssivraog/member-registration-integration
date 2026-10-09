using System.Net.Http.Headers;
using Azure.Core;

namespace MemberRegistration.Functions.Legacy;

/// <summary>
/// Adds an Entra ID access token for the legacy API to every attempt. Tokens are cached and renewed five
/// minutes before they expire, so a retry after a long backoff never goes out with an expired one.
/// </summary>
public sealed class BearerTokenHandler(TokenCredential credential, string scope, TimeProvider clock) : DelegatingHandler
{
    private static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private AccessToken? _cached;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await GetTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken);
    }

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (_cached is { } fresh && fresh.ExpiresOn - RenewBefore > clock.GetUtcNow())
        {
            return fresh.Token;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_cached is not { } current || current.ExpiresOn - RenewBefore <= clock.GetUtcNow())
            {
                _cached = await credential.GetTokenAsync(new TokenRequestContext([scope]), cancellationToken);
            }

            return _cached.Value.Token;
        }
        finally
        {
            _lock.Release();
        }
    }
}

/// <summary>Adds the legacy API key header to every attempt. The key is never logged.</summary>
public sealed class ApiKeyHandler(string headerName, string apiKey) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Remove(headerName);
        request.Headers.TryAddWithoutValidation(headerName, apiKey);
        return base.SendAsync(request, cancellationToken);
    }
}
