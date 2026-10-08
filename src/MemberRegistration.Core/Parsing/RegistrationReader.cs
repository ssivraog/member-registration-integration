using System.Text.Json;
using MemberRegistration.Core.Validation;

namespace MemberRegistration.Core.Parsing;

/// <summary>
/// Reads the CRM JSON by hand rather than deserialising, so a wrong-typed field is reported by name
/// instead of throwing, and unknown fields are ignored by construction.
/// </summary>
public static class RegistrationReader
{
    // Two values for one field (e.g. two emails) is ambiguous, so it is rejected rather than last-wins.
    private static readonly JsonDocumentOptions Options = new() { AllowDuplicateProperties = false };

    public static ReadResult Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Unreadable(ErrorCodes.BodyEmpty, "Request body is empty.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, Options);
        }
        catch (JsonException)
        {
            return Unreadable(ErrorCodes.BodyMalformed, "Request body is not valid JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Unreadable(ErrorCodes.BodyNotObject, "Request body must be a JSON object.");
            }

            var wrongType = new HashSet<string>();
            return new ReadResult.Read(new RawRegistration(
                ReadString(root, FieldNames.FirstName, wrongType),
                ReadString(root, FieldNames.LastName, wrongType),
                ReadString(root, FieldNames.DateOfBirth, wrongType),
                ReadString(root, FieldNames.Email, wrongType),
                ReadString(root, FieldNames.MembershipType, wrongType),
                wrongType));
        }
    }

    private static string? ReadString(JsonElement root, string name, HashSet<string> wrongType)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return value.GetString();
            case JsonValueKind.Null:
                return null;
            default:
                wrongType.Add(name);
                return null;
        }
    }

    private static ReadResult.Unreadable Unreadable(string code, string message) =>
        new(new ValidationError(FieldNames.Body, code, message));
}
