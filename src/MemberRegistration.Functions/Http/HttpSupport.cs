using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MemberRegistration.Core.Validation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace MemberRegistration.Functions.Http;

/// <summary>Request reading and problem responses shared by the HTTP functions.</summary>
internal static class HttpSupport
{
    public const int MaxBodyBytes = 64 * 1024;
    public const string ProblemJson = "application/problem+json";

    private static readonly JsonSerializerOptions ProblemOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static bool IsJson(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var mediaType)
        && (mediaType.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.Suffix.Equals("json", StringComparison.OrdinalIgnoreCase));

    /// <returns>The body as text, or null when it is larger than <see cref="MaxBodyBytes"/>.</returns>
    public static async Task<string?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
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

    public static ContentResult Problem(int status, string title, IReadOnlyList<ValidationError>? errors = null) => new()
    {
        StatusCode = status,
        ContentType = ProblemJson,
        Content = JsonSerializer.Serialize(new ProblemResponse(title, status, errors), ProblemOptions),
    };

    private sealed record ProblemResponse(string Title, int Status, IReadOnlyList<ValidationError>? Errors);
}
