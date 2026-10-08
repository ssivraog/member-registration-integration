using System.Text.Json.Serialization;

namespace MemberRegistration.Core.Contracts;

// Property order matches the legacy contract so the serialised body reads the same as the spec.
public sealed record LegacyMemberRequest(
    [property: JsonPropertyName("member")] LegacyMember Member);

public sealed record LegacyMember(
    [property: JsonPropertyName("given_name")] string GivenName,
    [property: JsonPropertyName("family_name")] string FamilyName,
    [property: JsonPropertyName("dob")] string Dob,
    [property: JsonPropertyName("contact")] LegacyContact Contact,
    [property: JsonPropertyName("plan_code")] string PlanCode,
    [property: JsonPropertyName("source")] string Source);

public sealed record LegacyContact(
    [property: JsonPropertyName("email")] string Email);
