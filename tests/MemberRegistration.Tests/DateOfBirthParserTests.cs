using System.Globalization;
using MemberRegistration.Core.Mapping;
using MemberRegistration.Core.Validation;
using static MemberRegistration.Tests.TestSupport;

namespace MemberRegistration.Tests;

public class DateOfBirthParserTests
{
    private static DateOfBirthParser Parser(DateTimeOffset? now = null) => new(new FixedClock(now ?? Now));

    [Theory]
    [InlineData("1990-04-12", 1990, 4, 12)]
    [InlineData(" 1990-04-12 ", 1990, 4, 12)]
    [InlineData("2000-02-29", 2000, 2, 29)]
    [InlineData("1990-04-12T00:00:00Z", 1990, 4, 12)]
    [InlineData("1990-04-12T09:15:00.123+10:00", 1990, 4, 12)]
    // Date part is taken as written: no time-zone shift into the previous or next day.
    [InlineData("1990-04-12T23:59:59-12:00", 1990, 4, 12)]
    [InlineData("1990-04-12t00:00:00z", 1990, 4, 12)]
    public void Accepts_iso_dates_and_date_times(string value, int year, int month, int day)
    {
        var error = Parser().TryParse(value, out var dateOfBirth);

        Assert.Null(error);
        Assert.Equal(new DateOnly(year, month, day), dateOfBirth);
    }

    [Theory]
    [InlineData("12/04/1990")]
    [InlineData("04/12/1990")]
    [InlineData("1990/04/12")]
    [InlineData("12-04-1990")]
    [InlineData("12.04.1990")]
    [InlineData("12 April 1990")]
    [InlineData("1990-4-12")]
    [InlineData("19900412")]
    [InlineData("1990-04-12 00:00:00")]
    [InlineData("1990-04-12Tgarbage")]
    [InlineData("١٩٩٠-٠٤-١٢")] // Arabic-Indic digits: \d would match these, the parser must not
    public void Rejects_other_formats_as_invalid_date_format(string value)
    {
        var error = Parser().TryParse(value, out _);

        Assert.Equal(ErrorCodes.InvalidDateFormat, error?.Code);
    }

    [Theory]
    [InlineData("1990-02-30")]
    [InlineData("1999-02-29")]
    [InlineData("1990-13-01")]
    [InlineData("1990-00-10")]
    [InlineData("1990-04-31T00:00:00Z")]
    public void Rejects_impossible_calendar_dates_as_invalid_date(string value)
    {
        var error = Parser().TryParse(value, out _);

        Assert.Equal(ErrorCodes.InvalidDate, error?.Code);
        Assert.Equal("dateOfBirth is not a real calendar date.", error?.Message);
    }

    [Theory]
    [InlineData("1899-12-31", false)]
    [InlineData("0990-04-12", false)]
    [InlineData("1900-01-01", true)]
    [InlineData("2026-06-01", true)]
    [InlineData("2026-06-02", false)]
    [InlineData("2090-01-01", false)]
    public void Enforces_the_1900_to_today_range(string value, bool accepted)
    {
        // Now is 2026-06-01T09:00Z, which is 2026-06-01 23:00 in UTC+14: 2 June is still the future.
        var error = Parser().TryParse(value, out _);

        if (accepted)
        {
            Assert.Null(error);
        }
        else
        {
            Assert.Equal(ErrorCodes.InvalidDate, error?.Code);
        }
    }

    [Fact]
    public void Today_is_judged_by_the_furthest_ahead_time_zone()
    {
        // 12:00Z on 1 June is already 2 June in UTC+10 to UTC+14, so a baby born then is valid.
        var noonUtc = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.Null(Parser(noonUtc).TryParse("2026-06-02", out _));
        Assert.Equal(ErrorCodes.InvalidDate, Parser(noonUtc).TryParse("2026-06-03", out _)?.Code);
    }

    [Theory]
    [InlineData(1990, 4, 12, "12/04/1990")]
    [InlineData(2001, 1, 5, "05/01/2001")]
    [InlineData(1900, 12, 31, "31/12/1900")]
    public void Formats_as_dd_MM_yyyy(int year, int month, int day, string expected)
    {
        Assert.Equal(expected, DateOfBirthParser.ToLegacy(new DateOnly(year, month, day)));
    }

    [Theory]
    [InlineData("en-US")] // month-first locale
    [InlineData("ar-SA")] // non-Gregorian default calendar
    [InlineData("de-DE")] // '.' date separator
    public void Formatting_ignores_the_server_culture(string culture)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);

            Assert.Null(Parser().TryParse("1990-04-12", out var dateOfBirth));
            Assert.Equal("12/04/1990", DateOfBirthParser.ToLegacy(dateOfBirth));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
