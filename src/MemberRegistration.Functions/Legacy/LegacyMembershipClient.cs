using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MemberRegistration.Core.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace MemberRegistration.Functions.Legacy;

/// <summary>What happened to a submission, as far as the caller needs to know.</summary>
public enum SubmissionOutcome
{
    /// <summary>The legacy API accepted the member (2xx).</summary>
    Accepted,

    /// <summary>The legacy API already has a member for this idempotency key (409).</summary>
    Duplicate,

    /// <summary>The legacy API refused the payload (other 4xx, except auth). Retrying won't help.</summary>
    Rejected,

    /// <summary>The legacy API refused our credentials (401/403): a configuration fault on this side.</summary>
    AuthFailed,

    /// <summary>Timed out, 5xx after retries, or the circuit is open. Safe to retry later with the same key.</summary>
    Unavailable,
}

public sealed record SubmissionResult(SubmissionOutcome Outcome, int? LegacyStatus);

public interface ILegacyMembershipClient
{
    Task<SubmissionResult> SubmitAsync(LegacyMemberRequest request, string idempotencyKey, string correlationId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Posts the legacy payload. Authentication and resilience are handlers on the typed HttpClient (see
/// Program.cs), so this class only builds the request and classifies the response.
/// </summary>
public sealed class LegacyMembershipClient(
    HttpClient http,
    IOptions<LegacyApiOptions> options,
    ILogger<LegacyMembershipClient> logger) : ILegacyMembershipClient
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const string CorrelationIdHeader = "X-Correlation-ID";

    public async Task<SubmissionResult> SubmitAsync(LegacyMemberRequest request, string idempotencyKey,
        string correlationId, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, options.Value.SubmitPath)
        {
            Content = new StringContent(LegacyJson.Serialize(request), Encoding.UTF8, "application/json"),
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.Add(IdempotencyKeyHeader, idempotencyKey);
        message.Headers.Add(CorrelationIdHeader, correlationId);

        try
        {
            using var response = await http.SendAsync(message, cancellationToken);
            var status = (int)response.StatusCode;
            var outcome = Classify(response.StatusCode);
            var level = outcome == SubmissionOutcome.Accepted ? LogLevel.Information
                : outcome == SubmissionOutcome.AuthFailed ? LogLevel.Error : LogLevel.Warning;
            // The response body may echo member details (PII), so only the status is logged.
            logger.Log(level, "Legacy API returned {Status} ({Outcome}) for correlation {CorrelationId}.", status, outcome, correlationId);
            return new SubmissionResult(outcome, status);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutRejectedException or BrokenCircuitException
                                       || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogWarning("Legacy API unavailable for correlation {CorrelationId}: {Reason}.", correlationId, ex.GetType().Name);
            return new SubmissionResult(SubmissionOutcome.Unavailable, null);
        }
    }

    private static SubmissionOutcome Classify(HttpStatusCode status) => (int)status switch
    {
        >= 200 and < 300 => SubmissionOutcome.Accepted,
        409 => SubmissionOutcome.Duplicate,
        401 or 403 => SubmissionOutcome.AuthFailed,
        408 or 429 => SubmissionOutcome.Unavailable,
        >= 400 and < 500 => SubmissionOutcome.Rejected,
        _ => SubmissionOutcome.Unavailable,
    };
}
