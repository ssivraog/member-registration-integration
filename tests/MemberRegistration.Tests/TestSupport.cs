using System.Text.Json.Nodes;
using MemberRegistration.Core;

namespace MemberRegistration.Tests;

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal static class TestSupport
{
    public static readonly DateTimeOffset Now = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    public const string SampleRegistration = """
        {
          "firstName": "Alex",
          "lastName": "Nguyen",
          "dateOfBirth": "1990-04-12",
          "email": "alex.nguyen@example.com",
          "membershipType": "Single",
          "registeredAt": "2026-06-01T09:00:00Z"
        }
        """;

    public const string ExpectedLegacyPayload = """
        {
          "member": {
            "given_name": "Alex",
            "family_name": "Nguyen",
            "dob": "12/04/1990",
            "contact": {
              "email": "alex.nguyen@example.com"
            },
            "plan_code": "S",
            "source": "MILKYWAY"
          }
        }
        """;

    public static RegistrationTranslator Translator(DateTimeOffset? now = null) => new(new FixedClock(now ?? Now));

    /// <summary>The sample registration with <paramref name="edit"/> applied.</summary>
    public static string Registration(Action<JsonObject> edit)
    {
        var json = JsonNode.Parse(SampleRegistration)!.AsObject();
        edit(json);
        return json.ToJsonString();
    }

    public static string Minified(string json) => JsonNode.Parse(json)!.ToJsonString();
}
