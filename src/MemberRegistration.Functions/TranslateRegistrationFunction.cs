using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MemberRegistration.Core;
using MemberRegistration.Core.Contracts;
using MemberRegistration.Core.Validation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace MemberRegistration.Functions;

/// <summary>HTTP adapter over <see cref="RegistrationTranslator"/>: status codes, body limits, logging.</summary>
public sealed class TranslateRegistrationFunction(
    RegistrationTranslator translator,
    ILogger<TranslateRegistrationFunction> logger)
{
    public const int MaxBodyBytes = 64 * 1024;
    private const string ProblemJson = "application/problem+json";

    private static readonly JsonSerializerOptions ProblemOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [Function("TranslateRegistration")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "registrations/legacy-payload")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
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

    private static bool IsJson(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var mediaType)
        && (mediaType.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.Suffix.Equals("json", StringComparison.OrdinalIgnoreCase));

    /// <returns>The body as text, or null when it is larger than <see cref="MaxBodyBytes"/>.</returns>
    private static async Task<string?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxBodyBytes)
        {
            return null;
        }

        // Content-Length can be absent (chunked), so the limit is also enforced while reading.
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static ContentResult Problem(int status, string title, IReadOnlyList<ValidationError>? errors = null) => new()
    {
        StatusCode = status,
        ContentType = ProblemJson,
        Content = JsonSerializer.Serialize(new ProblemResponse(title, status, errors), ProblemOptions),
    };

    private sealed record ProblemResponse(string Title, int Status, IReadOnlyList<ValidationError>? Errors);
}
