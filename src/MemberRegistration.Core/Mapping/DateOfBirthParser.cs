using System.Globalization;
using System.Text.RegularExpressions;
using MemberRegistration.Core.Validation;

namespace MemberRegistration.Core.Mapping;

public sealed partial class DateOfBirthParser(TimeProvider clock)
{
    public const string LegacyFormat = "dd/MM/yyyy";
    private const string CrmFormat = "yyyy-MM-dd";
    private static readonly DateOnly Earliest = new(1900, 1, 1);

    // Slash or dotted dates (12/04/1990) are deliberately not accepted: day/month order can't be
    // known, and a guessed DOB silently lands on a member record.
    [GeneratedRegex(@"^(?<date>[0-9]{4}-[0-9]{2}-[0-9]{2})(?<time>[Tt].+)?$")]
    private static partial Regex IsoShape();

    /// <returns>The validation error, or null when <paramref name="dateOfBirth"/> was parsed.</returns>
    public ValidationError? TryParse(string value, out DateOnly dateOfBirth)
    {
        dateOfBirth = default;
        var match = IsoShape().Match(value.Trim());
        if (!match.Success)
        {
            return FormatError();
        }

        if (!DateOnly.TryParseExact(match.Groups["date"].Value, CrmFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
        {
            return new ValidationError(FieldNames.DateOfBirth, ErrorCodes.InvalidDate,
                "dateOfBirth is not a real calendar date.");
        }

        // A date-time's date part is taken as written, never shifted across time zones.
        if (match.Groups["time"].Success
            && !DateTimeOffset.TryParse(match.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return FormatError();
        }

        if (parsed < Earliest || parsed > LatestTodayAnywhere())
        {
            return new ValidationError(FieldNames.DateOfBirth, ErrorCodes.InvalidDate,
                "dateOfBirth must be between 1900-01-01 and today.");
        }

        dateOfBirth = parsed;
        return null;
    }

    public static string ToLegacy(DateOnly date) => date.ToString(LegacyFormat, CultureInfo.InvariantCulture);

    // UTC+14 is the furthest-ahead zone: a baby born "today" in Australia can still be "tomorrow" in UTC.
    private DateOnly LatestTodayAnywhere() => DateOnly.FromDateTime(clock.GetUtcNow().AddHours(14).UtcDateTime);

    private static ValidationError FormatError() =>
        new(FieldNames.DateOfBirth, ErrorCodes.InvalidDateFormat, "dateOfBirth must be in yyyy-MM-dd format.");
}
