using System.Text.Json;
using System.Text.RegularExpressions;
using MemberRegistration.Core;
using MemberRegistration.Functions.Auth;
using MemberRegistration.Functions.Legacy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using static MemberRegistration.Functions.Http.HttpSupport;

namespace MemberRegistration.Functions;

/// <summary>
/// Authorizes the caller, translates the registration and submits it to the legacy membership API.
/// Callers must send an <c>Idempotency-Key</c> (the CRM record id): retries reuse it so the legacy side
/// never creates the member twice.
/// </summary>
public sealed partial class SubmitRegistrationFunction(
    RegistrationTranslator translator,
    IInboundAuthorizer authorizer,
    ILegacyMembershipClient legacy,
    ILogger<SubmitRegistrationFunction> logger)
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    // Opaque ids only: they go into outbound headers and logs, so no spaces, quotes or control characters.
    [GeneratedRegex("^[A-Za-z0-9._:-]{1,128}$")]
    private static partial Regex SafeId();

    [Function("SubmitRegistration")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "registrations")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await authorizer.AuthorizeAsync(request, AppRoles.Submit, cancellationToken) is { } denied)
            {
                return denied;
            }

            var idempotencyKey = request.Headers[LegacyMembershipClient.IdempotencyKeyHeader].ToString();
            if (!SafeId().IsMatch(idempotencyKey))
            {
                return Problem(StatusCodes.Status400BadRequest,
                    $"An {LegacyMembershipClient.IdempotencyKeyHeader} header (1-128 of A-Z a-z 0-9 . _ : -) is required.");
            }

            var suppliedCorrelation = request.Headers[LegacyMembershipClient.CorrelationIdHeader].ToString();
            var correlationId = SafeId().IsMatch(suppliedCorrelation) ? suppliedCorrelation : Guid.NewGuid().ToString("N");

            if (!IsJson(request.ContentType))
            {
                return Problem(StatusCodes.Status415UnsupportedMediaType, "Content-Type must be application/json.");
            }

            var body = await ReadBodyAsync(request, cancellationToken);
            if (body is null)
            {
                return Problem(StatusCodes.Status413PayloadTooLarge, $"Request body exceeds {MaxBodyBytes} bytes.");
            }

            switch (translator.Translate(body))
            {
                case TranslationResult.Failure failure:
                    logger.LogWarning("Registration {CorrelationId} rejected ({Kind}): {Errors}", correlationId, failure.Kind,
                        string.Join(", ", failure.Errors.Select(e => $"{e.Field}:{e.Code}")));
                    return failure.Kind == FailureKind.MalformedBody
                        ? Problem(StatusCodes.Status400BadRequest, "Request body could not be read.", failure.Errors)
                        : Problem(StatusCodes.Status422UnprocessableEntity, "Registration failed validation.", failure.Errors);

                case TranslationResult.Success success:
                    var result = await legacy.SubmitAsync(success.Request, idempotencyKey, correlationId, cancellationToken);
                    return Respond(result, correlationId);

                default:
                    throw new InvalidOperationException("Unknown translation result.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Unexpected failure submitting a registration.");
            return Problem(StatusCodes.Status500InternalServerError, "An unexpected error occurred.");
        }
    }

    // Legacy-side details (its body, our credentials) never reach the caller; the status codes say
    // whether a retry with the same Idempotency-Key makes sense (503) or not (409, 502).
    private static IActionResult Respond(SubmissionResult result, string correlationId) => result.Outcome switch
    {
        SubmissionOutcome.Accepted => Json(StatusCodes.Status202Accepted, new { status = "submitted", correlationId }),
        SubmissionOutcome.Duplicate => Json(StatusCodes.Status409Conflict, new { status = "duplicate", correlationId }),
        SubmissionOutcome.Rejected => Problem(StatusCodes.Status502BadGateway, "The legacy membership system rejected the registration."),
        SubmissionOutcome.AuthFailed => Problem(StatusCodes.Status502BadGateway, "The legacy membership system could not be called."),
        _ => Problem(StatusCodes.Status503ServiceUnavailable, "The legacy membership system is unavailable. Retry with the same Idempotency-Key."),
    };

    private static ContentResult Json(int status, object body) => new()
    {
        StatusCode = status,
        ContentType = "application/json",
        Content = JsonSerializer.Serialize(body, WebJson),
    };
}
