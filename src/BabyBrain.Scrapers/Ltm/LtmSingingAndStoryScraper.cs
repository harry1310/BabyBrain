using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp;
using BabyBrain.Scrapers.Domain;
using BabyBrain.Scrapers.Shared;

namespace BabyBrain.Scrapers.Ltm;

// Source: https://www.ltmuseum.co.uk/whats-on/family-events/singing-and-story-sessions
// A single recurring event on the London Transport Museum's Drupal site —
// "Singing, stories and crafts for under 5s". Not a listing.
//
// The site is behind Cloudflare, which fingerprints the TLS/HTTP2 stack and
// 403s a plain HttpClient even with browser-shaped headers — so we render
// through Playwright like the other CF-fronted sources. Two pieces of data
// live in two places:
//
//  * The event page carries the session schedule in "Key information" prose —
//    "When: 10:30, 11:30, 13:20, 14:20   Duration: 45 minutes" — and links to
//    the full date list via a "Show all dates" button.
//  * That linked page embeds a `cti_event.daysWithDates` JSON map of every
//    running date. This is the authoritative, term-time-aware schedule: it
//    already omits half-terms and holidays (e.g. no 29 May, no August), so we
//    use it verbatim rather than materialising every Friday ourselves.
//
// Each running date crossed with each of the four session times yields one
// row. Note the museum markets these as "free with Museum admission" — there
// is no separate session fee, but admission is required, so IsFree is false.
public sealed class LtmSingingAndStoryScraper : IScraper
{
    private const string EventUrl =
        "https://www.ltmuseum.co.uk/whats-on/family-events/singing-and-story-sessions";
    private const string Venue = "London Transport Museum";
    private const string Address = "Covent Garden Piazza, London";
    private const string Postcode = "WC2E 7BB";

    public string SourceId => "ltm_singing_and_story_sessions";
    public string Category => Categories.Museum;

    private readonly PlaywrightFetcher _fetcher;

    public LtmSingingAndStoryScraper(PlaywrightFetcher fetcher) => _fetcher = fetcher;

    public async Task<IReadOnlyList<EventOccurrence>> ScrapeAsync(int horizonDays, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var horizonEnd = today.AddDays(horizonDays);
        var now = DateTimeOffset.UtcNow;

        var html = await _fetcher.FetchRenderedHtmlAsync(EventUrl, "h1", ct: ct);
        var doc = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html), ct);

        var title = doc.QuerySelector("h1")?.TextContent?.Trim();
        if (string.IsNullOrEmpty(title))
            throw new InvalidOperationException("LTM: event title (h1) not found");

        // "Key information" is split across tags in the source; only the
        // rendered text content is contiguous enough to regex.
        var text = doc.Body?.TextContent ?? "";

        var sessions = ParseSessionTimes(text);
        if (sessions.Count == 0)
            throw new InvalidOperationException("LTM: no 'When:' session times found in page text");
        var duration = ParseDurationMinutes(text);

        var (minAge, maxAge) = TextParsing.ParseAgeRange(title);

        var description = doc.QuerySelector("meta[name=\"description\"]")?.GetAttribute("content")?.Trim();
        var notes = string.IsNullOrEmpty(description) ? null : Truncate(description, 400);

        // The "Show all dates" button links to the page carrying the full
        // term-time-aware date list.
        var allDatesHref = doc.QuerySelectorAll("a")
            .FirstOrDefault(a => string.Equals(a.TextContent.Trim(), "Show all dates",
                StringComparison.OrdinalIgnoreCase))
            ?.GetAttribute("href");
        if (string.IsNullOrEmpty(allDatesHref))
            throw new InvalidOperationException("LTM: 'Show all dates' link not found");

        var allDatesUrl = new Uri(new Uri(EventUrl), allDatesHref).ToString();
        var datesHtml = await _fetcher.FetchRenderedHtmlAsync(allDatesUrl, "h1", ct: ct);
        var dates = ParseRunningDates(datesHtml);
        if (dates.Count == 0)
            throw new InvalidOperationException($"LTM: no dates parsed from {allDatesUrl}");

        var rows = new List<EventOccurrence>();
        foreach (var date in dates)
        {
            if (date < today || date > horizonEnd) continue;
            foreach (var start in sessions)
            {
                rows.Add(new EventOccurrence
                {
                    ExternalKey = $"{SourceId}:{date:yyyy-MM-dd}:{start:HHmm}",
                    Source = SourceId,
                    Category = Category,
                    SourceUrl = EventUrl,
                    Date = date,
                    StartTime = start,
                    EndTime = duration is { } d ? start.AddMinutes(d) : null,
                    SessionName = title,
                    SessionNotes = notes,
                    VenueName = Venue,
                    VenueAddress = Address,
                    Postcode = Postcode,
                    MinAgeMonths = minAge,
                    MaxAgeMonths = maxAge,
                    // Dates come from an explicit term-time-aware list, so every
                    // row is a confirmed occurrence — no term-time caveat needed.
                    TermTimeOnly = false,
                    // "Free with Museum admission" — admission is required.
                    IsFree = false,
                    LastSeenAt = now,
                });
            }
        }
        return rows;
    }

    // "When: 10:30, 11:30, 13:20, 14:20" — grab the comma-separated run after
    // the "When:" label, then parse each HH:MM out of it.
    private static IReadOnlyList<TimeOnly> ParseSessionTimes(string text)
    {
        var m = Regex.Match(text, @"When:\s*((?:\d{1,2}:\d{2}\s*,?\s*)+)", RegexOptions.IgnoreCase);
        if (!m.Success) return Array.Empty<TimeOnly>();

        var times = new List<TimeOnly>();
        foreach (Match t in Regex.Matches(m.Groups[1].Value, @"\d{1,2}:\d{2}"))
        {
            if (TextParsing.ParseClockTime(t.Value) is { } parsed && !times.Contains(parsed))
                times.Add(parsed);
        }
        return times;
    }

    private static int? ParseDurationMinutes(string text)
    {
        var m = Regex.Match(text, @"Duration:\s*(\d+)\s*minute", RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out var d) ? d : null;
    }

    // The all-dates page embeds `"daysWithDates":{"2026-05-22":"Friday 22 May
    // 2026", ...}` — a flat map (no nested braces) of every running date.
    private static IReadOnlyList<DateOnly> ParseRunningDates(string html)
    {
        var block = Regex.Match(html, "\"daysWithDates\"\\s*:\\s*\\{([^}]*)\\}");
        if (!block.Success) return Array.Empty<DateOnly>();

        var dates = new List<DateOnly>();
        foreach (Match m in Regex.Matches(block.Groups[1].Value, "\"(\\d{4}-\\d{2}-\\d{2})\"\\s*:"))
        {
            if (DateOnly.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                dates.Add(d);
        }
        return dates;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";
}
