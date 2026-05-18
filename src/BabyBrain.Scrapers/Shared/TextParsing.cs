using System.Text.RegularExpressions;

namespace BabyBrain.Scrapers.Shared;

public static partial class TextParsing
{
    private static readonly Regex TimeRegex =
        new(@"^(\d{1,2})(?:[\.:](\d{2}))?\s*(am|pm|midday|noon|midnight)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static TimeOnly? ParseClockTime(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Trim().ToLowerInvariant().Replace(" ", "");
        if (t is "midday" or "noon") return new TimeOnly(12, 0);
        if (t is "midnight") return new TimeOnly(0, 0);

        var m = TimeRegex.Match(t);
        if (!m.Success) return null;
        var hour = int.Parse(m.Groups[1].Value);
        var minute = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
        var suffix = m.Groups[3].Value;
        if (suffix == "pm" && hour != 12) hour += 12;
        if (suffix == "am" && hour == 12) hour = 0;
        if (hour > 23 || minute > 59) return null;
        return new TimeOnly(hour, minute);
    }

    public static (int? min, int? max) ParseAgeRange(string text)
    {
        var t = text.ToLowerInvariant();
        var m = Regex.Match(t, @"(\d+)\s*to\s*(\d+)\s*months?");
        if (m.Success) return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
        // Catches "age 2-5", "ages 2-5", "aged 2-5" — all three V&A wordings seen
        // in the wild on the early-years listing and detail pages.
        m = Regex.Match(t, @"(?:ages?|aged)\s*(\d+)\s*(?:[–\-]|to)\s*(\d+)\b");
        if (m.Success) return (int.Parse(m.Groups[1].Value) * 12, int.Parse(m.Groups[2].Value) * 12);
        // "8–15-year-olds", "8-15 year olds", "8 to 15 year-olds"
        m = Regex.Match(t, @"(\d+)\s*(?:[–\-]|to)\s*(\d+)[\s\-]?year[\s\-]?olds?");
        if (m.Success) return (int.Parse(m.Groups[1].Value) * 12, int.Parse(m.Groups[2].Value) * 12);
        // "aged five and under", "five and under", "5s and under"
        m = Regex.Match(t, @"(?:aged\s+)?(\d+|one|two|three|four|five|six|seven|eight|nine|ten)s?\s+(?:year[\s\-]?olds?\s+)?(?:and|or)\s+under");
        if (m.Success && WordToNumber(m.Groups[1].Value) is int n1) return (0, n1 * 12);
        // "under fives", "under 5s", "under-5s" (hyphenated, as Southbank writes it)
        m = Regex.Match(t, @"under[\s\-]+(\d+|one|two|three|four|five|six|seven|eight|nine|ten)s?\b");
        if (m.Success && WordToNumber(m.Groups[1].Value) is int n2) return (0, n2 * 12);
        if (t.Contains("baby")) return (0, 12);
        if (t.Contains("toddler")) return (12, 36);
        return (null, null);
    }

    private static int? WordToNumber(string s)
    {
        if (int.TryParse(s, out var n)) return n;
        return s switch
        {
            "one" => 1, "two" => 2, "three" => 3, "four" => 4, "five" => 5,
            "six" => 6, "seven" => 7, "eight" => 8, "nine" => 9, "ten" => 10,
            _ => null,
        };
    }

    public static DayOfWeek? ParseDayOfWeek(string text) => text.Trim().ToLowerInvariant() switch
    {
        "monday" or "mondays" => DayOfWeek.Monday,
        "tuesday" or "tuesdays" => DayOfWeek.Tuesday,
        "wednesday" or "wednesdays" => DayOfWeek.Wednesday,
        "thursday" or "thursdays" => DayOfWeek.Thursday,
        "friday" or "fridays" => DayOfWeek.Friday,
        "saturday" or "saturdays" => DayOfWeek.Saturday,
        "sunday" or "sundays" => DayOfWeek.Sunday,
        _ => null,
    };

    // Extracts price intent from free text. "Free" wins outright; otherwise the
    // lowest £N amount found is returned as the headline (under the "tickets
    // from £X" mental model). Callers should strip out parenthetical fees /
    // booking surcharges before passing in — the helper itself can't tell a
    // £6 ticket from a £1 booking fee.
    public static (bool IsFree, decimal? Cost) ParsePrice(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (false, null);

        // Match "Free" as a standalone word — avoid e.g. "free wifi" but allow
        // "Free of charge" / "Free admission" / "Free entry".
        if (Regex.IsMatch(text, @"\bfree\b(?:\s+(?:of\s+charge|admission|entry|event))?", RegexOptions.IgnoreCase))
            return (true, null);

        decimal? min = null;
        foreach (Match m in Regex.Matches(text, @"£\s*(\d+(?:\.\d{1,2})?)"))
        {
            if (decimal.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
            {
                if (min is null || v < min) min = v;
            }
        }
        return (false, min);
    }

    public static IEnumerable<DateOnly> WeeklyDatesInWindow(DayOfWeek day, DateOnly from, DateOnly to)
    {
        var first = from;
        while (first.DayOfWeek != day) first = first.AddDays(1);
        for (var d = first; d <= to; d = d.AddDays(7))
            yield return d;
    }
}
