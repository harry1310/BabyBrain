using System.Text.RegularExpressions;
using AngleSharp;
using BabyBrain.Scrapers.Domain;
using BabyBrain.Scrapers.Shared;

namespace BabyBrain.Scrapers.MwHealth;

// Source: https://www.mwhealth.co.uk/baby---child---family-classes/
// Moon Women's Health publishes its baby/child/family classes as a single
// server-rendered HTML <table> — one row per class, columns Class | Day |
// More-link. No schema.org.
//
// The class cell reads "<name> (<age>) with <instructor>"; the day cell reads
// "<Weekday> at <start> - <end>". Each in-scope class is a weekly recurring
// session, materialised across the horizon like the Camden/Postal scrapers.
//
// Rows we skip:
//  * the header and the "31-Day Class Pass" promo — no weekday in the cell;
//  * "MONTHLY ..." classes (Family Yoga) — no determinable date;
//  * classes pitched older than under-5s (Teen Yoga) — caught by the age
//    ceiling once their non-numeric "(GCSE Years)" age fails to parse.
//
// The listing carries no per-class price (the studio sells a class pass), so
// Cost is left null / IsFree false — i.e. unknown.
public sealed class MwHealthClassesScraper : IScraper
{
    private const string ListingUrl = "https://www.mwhealth.co.uk/baby---child---family-classes/";
    private const string Venue = "Moon Women's Health";
    private const string Address = "63 Chetwynd Road, London";
    private const string Postcode = "NW5 1BX";

    // BabyBrain's baby/toddler ceiling — classes for older children are dropped.
    private const int MaxAgeCeilingMonths = 60;

    public string SourceId => "mw_health_classes";
    public string Category => Categories.Exercise;

    private readonly HttpClient _http;

    public MwHealthClassesScraper(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<EventOccurrence>> ScrapeAsync(int horizonDays, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var horizonEnd = today.AddDays(horizonDays);
        var now = DateTimeOffset.UtcNow;

        var html = await _http.GetStringAsync(ListingUrl, ct);
        var doc = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html), ct);

        var rows = new List<EventOccurrence>();
        foreach (var tr in doc.QuerySelectorAll("table tr"))
        {
            ct.ThrowIfCancellationRequested();

            var cells = tr.QuerySelectorAll("td");
            if (cells.Length < 2) continue; // header / malformed

            rows.AddRange(BuildRows(
                cells[0].TextContent.Trim(), cells[1].TextContent.Trim(),
                today, horizonEnd, now));
        }
        return rows;
    }

    private IEnumerable<EventOccurrence> BuildRows(string classText, string dayText,
        DateOnly from, DateOnly to, DateTimeOffset now)
    {
        // Monthly classes carry no determinable date.
        if (Regex.IsMatch(dayText, @"\bmonthly\b", RegexOptions.IgnoreCase)) yield break;

        var day = FindWeekday(dayText);
        if (day is null) yield break; // e.g. the "31-Day Class Pass" promo row

        var (start, end) = ParseTimeRange(dayText);
        if (start is null) yield break;

        var (minAge, maxAge) = ParseAge(classText);
        // Drop classes outside the baby/toddler range (e.g. teen yoga, whose
        // "(GCSE Years)" age doesn't parse and so leaves maxAge null).
        if (maxAge is null || maxAge > MaxAgeCeilingMonths) yield break;

        var name = ClassName(classText);
        if (name.Length == 0) yield break;

        foreach (var date in TextParsing.WeeklyDatesInWindow(day.Value, from, to))
        {
            yield return new EventOccurrence
            {
                ExternalKey = $"{SourceId}:{Slug(name)}:{date:yyyy-MM-dd}:{start:HHmm}",
                Source = SourceId,
                Category = Category,
                SourceUrl = ListingUrl,
                Date = date,
                StartTime = start.Value,
                EndTime = end,
                SessionName = name,
                VenueName = Venue,
                VenueAddress = Address,
                Postcode = Postcode,
                MinAgeMonths = minAge,
                MaxAgeMonths = maxAge,
                TermTimeOnly = false,
                IsFree = false, // paid classes; the listing publishes no price
                LastSeenAt = now,
            };
        }
    }

    // "<name> (<age>) with <instructor>" → the display name. Drop the trailing
    // "with <instructor>" (split on the *last* " with " — class names like
    // "In Tune with Your Little Ones" contain "with" themselves) and the
    // trailing "(<age>)" descriptor.
    private static string ClassName(string classText)
    {
        var name = classText;
        var withIdx = name.LastIndexOf(" with ", StringComparison.OrdinalIgnoreCase);
        if (withIdx > 0) name = name[..withIdx];
        name = Regex.Replace(name, @"\s*\([^)]*\)\s*$", "");
        return name.Trim();
    }

    private static DayOfWeek? FindWeekday(string text)
    {
        var m = Regex.Match(text,
            @"\b(monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b",
            RegexOptions.IgnoreCase);
        return m.Success ? TextParsing.ParseDayOfWeek(m.Groups[1].Value) : null;
    }

    // "<Weekday> at 11am - 12pm" / "... 9.15am to 10am" — first two clock
    // tokens are the start and end.
    private static (TimeOnly? start, TimeOnly? end) ParseTimeRange(string text)
    {
        var times = Regex.Matches(text, @"\d{1,2}(?:[.:]\d{2})?\s*(?:am|pm)", RegexOptions.IgnoreCase)
            .Select(m => TextParsing.ParseClockTime(m.Value))
            .Where(t => t is not null)
            .Select(t => t!.Value)
            .ToList();
        if (times.Count == 0) return (null, null);
        return (times[0], times.Count > 1 ? times[1] : null);
    }

    // Age sits in the class cell's first "(...)": "(6 to 14 months)",
    // "(under 6 months)", "(under 3 years)", "(pre-crawlers)". Non-numeric
    // descriptors ("(GCSE Years)") fall through to (null, null).
    private static (int? min, int? max) ParseAge(string classText)
    {
        var paren = Regex.Match(classText, @"\(([^)]*)\)");
        if (!paren.Success) return (null, null);
        var t = paren.Groups[1].Value.Trim().ToLowerInvariant();

        var r = Regex.Match(t, @"(\d+)\s*(?:to|[-–])\s*(\d+)\s*months?");
        if (r.Success) return (int.Parse(r.Groups[1].Value), int.Parse(r.Groups[2].Value));

        r = Regex.Match(t, @"(\d+)\s*(?:to|[-–])\s*(\d+)\s*years?");
        if (r.Success) return (int.Parse(r.Groups[1].Value) * 12, int.Parse(r.Groups[2].Value) * 12);

        r = Regex.Match(t, @"under\s*(\d+)\s*months?");
        if (r.Success) return (0, int.Parse(r.Groups[1].Value));

        r = Regex.Match(t, @"under\s*(\d+)\s*years?");
        if (r.Success) return (0, int.Parse(r.Groups[1].Value) * 12);

        // "pre-crawlers" — youngest babies, not yet mobile.
        if (t.Contains("pre-crawler") || t.Contains("precrawler"))
            return (0, 12);

        return (null, null);
    }

    private static string Slug(string s) =>
        Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]+", "_").Trim('_');
}
