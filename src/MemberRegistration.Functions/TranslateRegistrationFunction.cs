using MemberRegistration.Core;
using MemberRegistration.Core.Contracts;
using MemberRegistration.Functions.Auth;
using MemberRegistration.Functions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using static MemberRegistration.Functions.Http.HttpSupport;

namespace MemberRegistration.Functions;

/// <summary>HTTP adapter over <see cref="RegistrationTranslator"/>: auth, status codes, body limits, logging.</summary>
public sealed class TranslateRegistrationFunction(
    RegistrationTranslator translator,
    IInboundAuthorizer authorizer,
    ILogger<TranslateRegistrationFunction> logger)
{
    public const int MaxBodyBytes = HttpSupport.MaxBodyBytes;

    // The function key is kept as a second layer; the caller's identity and role come from the bearer token.
    [Function("TranslateRegistration")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "registrations/legacy-payload")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            // Before the body is read: an unauthenticated caller learns nothing about validation.
            if (await authorizer.AuthorizeAsync(request, AppRoles.Translate, cancellationToken) is { } denied)
            {
                return denied;
            }

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
                case TranslationResult.Success success:
                    logger.LogInformation("Registration translated to legacy payload.");
                    return new ContentResult
                    {
                        StatusCode = StatusCodes.Status200OK,
                        ContentType = "application/json",
                        Content = LegacyJson.Serialize(success.Request),
                    };

                case TranslationResult.Failure failure:
                    // Field names and codes only: the values are PII and stay out of the logs.
                    logger.LogWarning("Registration rejected ({Kind}): {Errors}", failure.Kind,
                        string.Join(", ", failure.Errors.Select(e => $"{e.Field}:{e.Code}")));
                    return failure.Kind == FailureKind.MalformedBody
                        ? Problem(StatusCodes.Status400BadRequest, "Request body could not be read.", failure.Errors)
                        : Problem(StatusCodes.Status422UnprocessableEntity, "Registration failed validation.", failure.Errors);

                default:
                    throw new InvalidOperationException("Unknown translation result.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Unexpected failure translating a registration.");
            return Problem(StatusCodes.Status500InternalServerError, "An unexpected error occurred.");
        }
    }
}
