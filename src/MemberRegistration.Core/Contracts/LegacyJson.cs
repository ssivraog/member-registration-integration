using System.Text.Encodings.Web;
using System.Text.Json;

namespace MemberRegistration.Core.Contracts;

public static class LegacyJson
{
    // The default encoder escapes '+' and non-ASCII (e.g. "Zoë") as \uXXXX: valid JSON, but older
    // parsers often mishandle it, and this body is never embedded in HTML.
    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(LegacyMemberRequest request) => JsonSerializer.Serialize(request, Options);
}
