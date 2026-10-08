namespace MemberRegistration.Core.Mapping;

public static class PlanCodeMapper
{
    private static readonly Dictionary<string, string> Codes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Single"] = "S",
        ["Couple"] = "C",
        ["Family"] = "F",
    };

    public static IReadOnlyCollection<string> KnownTypes { get; } = ["Single", "Couple", "Family"];

    public static bool TryMap(string membershipType, out string planCode)
    {
        if (Codes.TryGetValue(membershipType.Trim(), out var code))
        {
            planCode = code;
            return true;
        }

        planCode = string.Empty;
        return false;
    }
}
