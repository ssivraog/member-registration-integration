using System.Text.RegularExpressions;

namespace MemberRegistration.Core.Mapping;

/// <summary>A plausibility check, not a deliverability one: one '@', no whitespace, a dotted domain.</summary>
public static partial class EmailRule
{
    private const int MaxLength = 254;

    [GeneratedRegex(@"^[^@\s]+@[^@\s.]+(\.[^@\s.]+)+$")]
    private static partial Regex Shape();

    public static bool IsPlausible(string email) => email.Length <= MaxLength && Shape().IsMatch(email);
}
