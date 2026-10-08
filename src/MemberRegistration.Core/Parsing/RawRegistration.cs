namespace MemberRegistration.Core.Parsing;

/// <summary>The registration fields exactly as received: untrimmed, unvalidated.</summary>
public sealed record RawRegistration(
    string? FirstName,
    string? LastName,
    string? DateOfBirth,
    string? Email,
    string? MembershipType,
    IReadOnlySet<string> WrongTypeFields);
