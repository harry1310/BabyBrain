using System.Text.RegularExpressions;
using AngleSharp;
using BabyBrain.Scrapers.Domain;
using BabyBrain.Scrapers.Shared;

namespace BabyBrain.Scrapers.LittleBoo;

// Source: https://www.littleboostories.com/baby-classes-highbury
// Little Boo Stories runs two back-to-back weekly storytelling classes every
// Monday at Christ Church Highbury — a younger "Mini Boo Stories" (6-18 months)
// and an older "Boo Story" (18 months-4 years). It's a Wix build (hashed,
// unstable CSS class names), but the facts we need sit in plain visible text we
// can anchor on, one line per class:
//   "Boo Story – 18 months to 4 years | 10:00am"
//   "Mini Boo Stories – 6 to 18 months | 11:00am"
// plus "...Mondays in Highbury." for the day. Venue/address are fixed (one
// location) so they're constants; booking is off-site (Happity) and the page
// shows no price, so Cost is left unknown.
//
// We fetch the page (plain browser-UA HttpClient, with retries — the Wix edge is
// flaky), require the venue anchor to still be present (else the page was rebuilt
// → fail loudly into a claude-fix issue), parse each class line, and materialise
// one row per weekly occurrence across the horizon — the same recurring model as
// the Tempo Tots / Camden timetables.
public sealed class LittleBooStoriesHighburyScraper : IScraper
{
    private const string PageUrl = "https://www.littleboostories.com/baby-classes-highbury";
    private const string Venue = "Christ Church Highbury";
    private const string VenueAnchor = "Christ Church"; // liveness guard
    private const string Address = "155 Highbury Grove, London";
    private const string Postcode = "N5 1SA";

    public string SourceId => "little_boo_stories_highbury";
    public string Category => Categories.Class;

    // Wix flakiness mitigation: retry the page fetch a few times before failing.
    private const int FetchAttempts = 4;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly HttpClient _http;

    public LittleBooStoriesHighburyScraper(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<EventOccurrence>> ScrapeAsync(int horizonDays, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var horizonEnd = today.AddDays(horizonDays);
        var now = DateTimeOffset.UtcNow;

        var html = await FetchPageWithRetriesAsync(ct);
        var doc = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html), ct);

        // Collapse the page's visible text to one whitespace-normalised string so
        // our anchors aren't defeated by Wix's generous spacing; AngleSharp has
        // already decoded entities (so the "–" separators read as real dashes).
        // Wix sprinkles zero-width characters between words, so strip those first
        // or they'd break the anchors below.
        var raw = (doc.Body?.TextContent ?? "").Replace("​", "").Replace("‌", "")
            .Replace("‍", "").Replace("﻿", "");
        var text = Regex.Replace(raw, @"\s+", " ").Trim();

        // Liveness guard: if the venue is gone the page has been rebuilt and the
        // anchors below can't be trusted — fail so the source gets re-checked.
        if (!text.Contains(VenueAnchor, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Little Boo Stories: venue anchor '{VenueAnchor}' not found — page structure may have changed.");

        var day = ParseDay(text)
            ?? throw new InvalidOperationException("Little Boo Stories: could not find the class day on the page.");

        var classes = ParseClasses(text);
        if (classes.Count == 0)
            throw new InvalidOperationException("Little Boo Stories: no class lines parsed — page structure may have changed.");

        var rows = new List<EventOccurrence>();
        foreach (var cls in classes)
        {
            foreach (var date in TextParsing.WeeklyDatesInWindow(day, today, horizonEnd))
            {
                rows.Add(new EventOccurrence
                {
                    ExternalKey = $"{SourceId}:{Slug(cls.Name)}:{date:yyyy-MM-dd}:{cls.Start:HHmm}",
                    Source = SourceId,
                    Category = Category,
                    SourceUrl = PageUrl,
                    Date = date,
                    StartTime = cls.Start,
                    EndTime = null,
                    TimeApproximate = false,
                    SessionName = cls.Name,
                    SessionNotes = "Weekly storytelling class — puppetry, music, movement and imagination. "
                        + "Booking via Happity (links on the page); check there for exact dates and any holiday breaks.",
                    VenueName = Venue,
                    VenueAddress = Address,
                    Postcode = Postcode,
                    MinAgeMonths = cls.MinAge,
                    MaxAgeMonths = cls.MaxAge,
                    TermTimeOnly = false,
                    IsFree = false,
                    Cost = null, // no price published on the page; booking is off-site
                    LastSeenAt = now,
                });
            }
        }
        return rows;
    }

    private record ClassInfo(string Name, TimeOnly Start, int? MinAge, int? MaxAge);

    // Each class is one visible line: "<Name> — <age range> | <h:mm>am/pm" (the
    // separator is an em-dash, but we don't depend on which dash it is — capture
    // everything up to the pipe as the age text and let ParseAgeText read the
    // numbers). Anchor on the two known class names (longest first so "Mini Boo
    // Stories" wins over "Boo Story").
    private static IReadOnlyList<ClassInfo> ParseClasses(string text)
    {
        var found = new List<ClassInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rx = new Regex(
            @"(Mini Boo Stories|Boo Story)\s+([^|]+?)\s*\|\s*(\d{1,2}):(\d{2})\s*([ap]m)",
            RegexOptions.IgnoreCase);

        foreach (Match m in rx.Matches(text))
        {
            var name = m.Groups[1].Value.Trim();
            if (!seen.Add(name)) continue; // page repeats the line in og/meta + body

            if (ParseClock(m.Groups[3].Value, m.Groups[4].Value, m.Groups[5].Value) is not TimeOnly start)
                continue;
            var (min, max) = ParseAgeText(m.Groups[2].Value);
            found.Add(new ClassInfo(name, start, min, max));
        }
        return found;
    }

    // The body says "...comes to Highbury every Monday at Christ Church..." —
    // anchor on "every <weekday>" so we don't grab a stray weekday from elsewhere
    // on the page; fall back to "<weekday>s in Highbury" (the page's meta wording).
    private const string Weekdays = "Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday";

    private static DayOfWeek? ParseDay(string text)
    {
        var m = Regex.Match(text, $@"every\s+({Weekdays})", RegexOptions.IgnoreCase);
        if (!m.Success)
            m = Regex.Match(text, $@"({Weekdays})s?\s+in\s+Highbury", RegexOptions.IgnoreCase);
        return m.Success ? TextParsing.ParseDayOfWeek(m.Groups[1].Value) : null;
    }

    // Handles "18 months to 4 years" (→ 18-48) and "6 to 18 months" (→ 6-18):
    // a missing unit on the first number inherits the second's unit.
    private static (int? min, int? max) ParseAgeText(string s)
    {
        var m = Regex.Match(s,
            @"(\d{1,2})\s*(month|year)?s?\s*to\s*(\d{1,2})\s*(month|year)s?",
            RegexOptions.IgnoreCase);
        if (!m.Success) return TextParsing.ParseAgeRange(s);

        var n1 = int.Parse(m.Groups[1].Value);
        var n2 = int.Parse(m.Groups[3].Value);
        var u1 = m.Groups[2].Value.Length > 0 ? m.Groups[2].Value : m.Groups[4].Value;
        var u2 = m.Groups[4].Value;
        var min = u1.Equals("year", StringComparison.OrdinalIgnoreCase) ? n1 * 12 : n1;
        var max = u2.Equals("year", StringComparison.OrdinalIgnoreCase) ? n2 * 12 : n2;
        return (min, max);
    }

    private static TimeOnly? ParseClock(string h, string m, string ampm)
    {
        if (!int.TryParse(h, out var hour) || !int.TryParse(m, out var minute)) return null;
        var pm = ampm.Equals("pm", StringComparison.OrdinalIgnoreCase);
        if (pm && hour != 12) hour += 12;
        if (!pm && hour == 12) hour = 0;
        if (hour > 23 || minute > 59) return null;
        return new TimeOnly(hour, minute);
    }

    private static string Slug(string name) =>
        Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');

    // Fetch the page, retrying on any HTTP failure (the Wix edge intermittently
    // 404s); a genuine cancellation propagates.
    private async Task<string> FetchPageWithRetriesAsync(CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= FetchAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await _http.GetStringAsync(PageUrl, ct);
            }
            catch (Exception ex) when (
                ex is HttpRequestException ||
                (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                last = ex;
                if (attempt < FetchAttempts)
                    await Task.Delay(RetryDelay, ct);
            }
        }
        throw new InvalidOperationException(
            $"Little Boo Stories: page fetch failed after {FetchAttempts} attempts.", last);
    }
}
