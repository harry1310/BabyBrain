using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using BabyBrain.Scrapers.Domain;
using BabyBrain.Scrapers.Shared;

namespace BabyBrain.Scrapers.CityOfLondon;

// Source: https://www.cityoflondon.gov.uk/services/libraries/regular-library-activities-for-children
// Server-rendered HTML (UTF-8, behind a CDN that wants a browser UA, no JS
// challenge) so a plain HttpClient is enough — no Playwright.
//
// The page is an accordion: one `<button class="AccordionButton…">` per library
// ("Activities at <Library>"), and the button's sibling <div> holds a flat run
// of <h3> (session name) followed by <p> lines. The first <p>s after an <h3>
// carry the schedule ("Thursdays 10–10.30am", or "Last Thursday of the month
// 10.30-11.30am"); a later <p> carries the age range; the rest are blurb.
// Sessions recur weekly (or monthly for the "Last <day>" wording); we
// materialise concrete occurrences across the horizon. All sessions are free.
public sealed class CityOfLondonLibrariesScraper : IScraper
{
    private const string Url =
        "https://www.cityoflondon.gov.uk/services/libraries/regular-library-activities-for-children";

    public string SourceId => "city_of_london_libraries";
    public string Category => Categories.Library;

    private readonly HttpClient _http;

    public CityOfLondonLibrariesScraper(HttpClient http) => _http = http;

    // Addresses aren't on the listing page; these three City of London library
    // venues are stable, so we stamp known postcodes for geocoding. An unlisted
    // future library still yields rows — just without an address.
    private static readonly Dictionary<string, (string Address, string Postcode)> Venues =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Artizan Street Library"] = ("Artizan Street, London", "E1 7AF"),
            ["Barbican Children's Library"] = ("Barbican Centre, Silk Street, London", "EC2Y 8DS"),
            ["Shoe Lane Library"] = ("Hill House, Little New Street, London", "EC4A 3JR"),
        };

    public async Task<IReadOnlyList<EventOccurrence>> ScrapeAsync(int horizonDays, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var horizonEnd = today.AddDays(horizonDays);
        var now = DateTimeOffset.UtcNow;
        var rows = new List<EventOccurrence>();

        var html = await _http.GetStringAsync(Url, ct);
        var doc = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html), ct);

        // Match on the styled-components class prefix, not the per-build hash.
        var buttons = doc.QuerySelectorAll("button[class*='AccordionButton']");
        if (buttons.Length == 0)
            throw new InvalidOperationException("City of London: no AccordionButton elements found");

        foreach (var button in buttons)
        {
            var venue = StripPrefix(button.TextContent.Trim());
            var content = button.NextElementSibling;
            if (content is null) continue;

            string? currentSession = null;
            var schedule = new List<Schedule>();
            var blurb = new List<string>();
            string? ageText = null;

            foreach (var node in content.Children)
            {
                if (node.NodeName == "H3")
                {
                    Flush(currentSession, schedule, ageText, blurb, venue, today, horizonEnd, now, rows);
                    currentSession = Clean(node.TextContent);
                    schedule = new List<Schedule>();
                    blurb = new List<string>();
                    ageText = null;
                }
                else if (node.NodeName == "P" && currentSession is not null)
                {
                    var text = Clean(node.TextContent);
                    if (text.Length == 0) continue;
                    if (ParseSchedule(text) is { } s) schedule.Add(s);
                    else if (ageText is null && text.Contains("age", StringComparison.OrdinalIgnoreCase)) ageText = text;
                    else blurb.Add(text);
                }
            }
            Flush(currentSession, schedule, ageText, blurb, venue, today, horizonEnd, now, rows);
        }
        return rows;
    }

    private void Flush(
        string? session, List<Schedule> schedule, string? ageText, List<string> blurb,
        string venue, DateOnly from, DateOnly to, DateTimeOffset now, List<EventOccurrence> rows)
    {
        if (session is null || schedule.Count == 0) return;

        var (minAge, maxAge) = ParseAge(ageText);
        var notes = blurb.Count > 0 ? string.Join(" ", blurb) : null;
        var (address, postcode) = Venues.TryGetValue(venue, out var v) ? v : ((string?)null, (string?)null);

        foreach (var s in schedule)
        {
            var dates = s.Monthly
                ? LastWeekdayOfMonthInWindow(s.Day, from, to)
                : TextParsing.WeeklyDatesInWindow(s.Day, from, to);
            foreach (var date in dates)
            {
                rows.Add(new EventOccurrence
                {
                    ExternalKey = $"{SourceId}:{Slug(venue)}:{Slug(session)}:{date:yyyy-MM-dd}:{s.Start:HHmm}",
                    Source = SourceId,
                    Category = Category,
                    SourceUrl = Url,
                    Date = date,
                    StartTime = s.Start,
                    EndTime = s.End,
                    SessionName = session,
                    SessionNotes = notes,
                    VenueName = venue,
                    VenueAddress = address,
                    Postcode = postcode,
                    MinAgeMonths = minAge,
                    MaxAgeMonths = maxAge,
                    TermTimeOnly = false,
                    IsFree = true,
                    LastSeenAt = now,
                });
            }
        }
    }

    private record Schedule(bool Monthly, DayOfWeek Day, TimeOnly Start, TimeOnly? End);

    // "Thursdays 10–10.30am" / "Tuesdays 2-6pm" / "Last Thursday of the month 10.30-11.30am".
    // The start time's am/pm suffix is often omitted and inherited from the end.
    private static readonly Regex ScheduleRegex = new(
        @"^Last\s+(?<mday>Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday)\s+of\s+the\s+month\s+(?<body>.+)$|" +
        @"^(?<wday>Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday)s?\s+(?<body>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RangeRegex = new(
        @"^(?<start>\d{1,2}(?:[.:]\d{2})?)\s*(?<ssuf>am|pm)?\s*[-–—]\s*(?<end>\d{1,2}(?:[.:]\d{2})?)\s*(?<esuf>am|pm)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static Schedule? ParseSchedule(string line)
    {
        var m = ScheduleRegex.Match(line);
        if (!m.Success) return null;
        var monthly = m.Groups["mday"].Success;
        var day = TextParsing.ParseDayOfWeek(monthly ? m.Groups["mday"].Value : m.Groups["wday"].Value);
        if (day is null) return null;

        var r = RangeRegex.Match(m.Groups["body"].Value.Trim());
        if (!r.Success) return null;
        var endSuf = r.Groups["esuf"].Value;
        var startSuf = r.Groups["ssuf"].Success && r.Groups["ssuf"].Value.Length > 0 ? r.Groups["ssuf"].Value : endSuf;
        var start = TextParsing.ParseClockTime(r.Groups["start"].Value + startSuf);
        var end = TextParsing.ParseClockTime(r.Groups["end"].Value + endSuf);
        if (start is null || end is null) return null;
        // Bare start inheriting the end's suffix can land after the end (e.g.
        // "11–1pm" → 11pm–13:00); in that case the start is the morning half.
        if (start > end && startSuf == endSuf)
            start = TextParsing.ParseClockTime(r.Groups["start"].Value + "am") ?? start;
        return new Schedule(monthly, day.Value, start.Value, end);
    }

    private static (int? min, int? max) ParseAge(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);
        var (min, max) = TextParsing.ParseAgeRange(text);
        if (min is not null || max is not null) return (min, max);
        // "Ages 3+" — open-ended minimum the shared helper doesn't cover.
        var m = Regex.Match(text, @"(\d+)\s*\+");
        return m.Success ? (int.Parse(m.Groups[1].Value) * 12, null) : (null, null);
    }

    private static IEnumerable<DateOnly> LastWeekdayOfMonthInWindow(DayOfWeek day, DateOnly from, DateOnly to)
    {
        var month = new DateOnly(from.Year, from.Month, 1);
        while (month <= to)
        {
            var last = new DateOnly(month.Year, month.Month, DateTime.DaysInMonth(month.Year, month.Month));
            while (last.DayOfWeek != day) last = last.AddDays(-1);
            if (last >= from && last <= to) yield return last;
            month = month.AddMonths(1);
        }
    }

    private static string StripPrefix(string s) =>
        Regex.Replace(s, @"^Activities\s+at\s+", "", RegexOptions.IgnoreCase).Trim();

    // Collapse whitespace (incl. the &nbsp; the CMS sprinkles everywhere).
    private static string Clean(string s) => Regex.Replace(s.Replace(' ', ' '), @"\s+", " ").Trim();

    private static string Slug(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
}
