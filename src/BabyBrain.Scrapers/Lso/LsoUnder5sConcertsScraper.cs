using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using BabyBrain.Scrapers.Domain;
using BabyBrain.Scrapers.Shared;

namespace BabyBrain.Scrapers.Lso;

// Source: https://www.lso.co.uk/whats-on/?category=family-events
// The London Symphony Orchestra's what's-on calendar, filtered to its
// "family-events" category. Server-rendered WordPress HTML, no schema.org.
//
// That category mixes two audiences: "Family Concert" events recommended for
// ages 7-12 (above BabyBrain's baby/toddler remit) and "Musical Storytelling
// for Under-5s" concerts at LSO St Luke's. We keep only the under-5s ones —
// matched on the card title — and drop the 7-12 concerts.
//
// Each under-5s concert card packs two showings into one date line, e.g.
// "Saturday 14 November 2026 · 10.30am & 12pm" — we emit one row per showing.
//
// Horizon note: LSO announces these concerts far in advance and runs only a
// handful a year, so every upcoming one is typically well beyond the 90-day
// scrape horizon. We therefore emit every future-dated showing the page lists
// and deliberately ignore horizonDays — filtering to 90 days would emit
// nothing and trip the "0 events = failure" rule for a source working fine.
//
// Price (£N children) comes from each concert's detail page; a detail-page
// failure is swallowed and simply leaves Cost null.
public sealed class LsoUnder5sConcertsScraper : IScraper
{
    private const string ListingUrl = "https://www.lso.co.uk/whats-on/?category=family-events";
    private const string Venue = "LSO St Luke's";
    private const string Address = "161 Old Street, London";
    private const string Postcode = "EC1V 9NG";

    // Under-5s → 0-60 months. LSO publishes no lower bound (babies under 12
    // months attend free, no ticket), so min is 0.
    private const int MinAge = 0;
    private const int MaxAge = 60;

    // Published end times ("the concerts will finish at approximately 11.15am
    // and 12.45pm") put each showing at roughly 45 minutes.
    private const int DurationMinutes = 45;

    public string SourceId => "lso_under_5s_concerts";
    public string Category => Categories.Concert;

    private readonly HttpClient _http;

    public LsoUnder5sConcertsScraper(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<EventOccurrence>> ScrapeAsync(int horizonDays, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var now = DateTimeOffset.UtcNow;

        var html = await _http.GetStringAsync(ListingUrl, ct);
        var doc = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html), ct);

        var rows = new List<EventOccurrence>();
        foreach (var card in doc.QuerySelectorAll("div.c-event-card"))
        {
            ct.ThrowIfCancellationRequested();

            var title = card.QuerySelector("h3.c-event-card__title")?.TextContent?.Trim();
            if (string.IsNullOrEmpty(title)) continue;

            // Keep only the under-5s concerts; skip the 7-12 "Family Concert" cards.
            if (!Regex.IsMatch(title, @"under[\s\-]?5", RegexOptions.IgnoreCase)) continue;

            var href = card.QuerySelector("a.c-event-card__link")?.GetAttribute("href");
            if (string.IsNullOrEmpty(href)) continue;

            var dateText = card.QuerySelector("p.c-event-card__date")?.TextContent?.Trim();
            if (string.IsNullOrEmpty(dateText)) continue;

            var date = ParseDate(dateText);
            // Skip past concerts; emit every future one regardless of horizon.
            if (date is null || date < today) continue;

            var times = ParseTimes(dateText);
            if (times.Count == 0) continue;

            var excerpt = card.QuerySelector("p.c-event-card__excerpt")?.TextContent?.Trim();
            var notes = string.IsNullOrEmpty(excerpt) ? null : Truncate(excerpt, 400);

            var cost = await TryGetChildPriceAsync(href, ct);
            var slug = SlugFromUrl(href);

            foreach (var start in times)
            {
                rows.Add(new EventOccurrence
                {
                    ExternalKey = $"{SourceId}:{slug}:{date:yyyy-MM-dd}:{start:HHmm}",
                    Source = SourceId,
                    Category = Category,
                    SourceUrl = href,
                    Date = date.Value,
                    StartTime = start,
                    EndTime = start.AddMinutes(DurationMinutes),
                    SessionName = title,
                    SessionNotes = notes,
                    VenueName = Venue,
                    VenueAddress = Address,
                    Postcode = Postcode,
                    MinAgeMonths = MinAge,
                    MaxAgeMonths = MaxAge,
                    TermTimeOnly = false,
                    IsFree = false,
                    Cost = cost,
                    LastSeenAt = now,
                });
            }
        }

        // ExternalKey is unique; collapse in case a card is ever listed twice.
        return rows
            .GroupBy(r => r.ExternalKey)
            .Select(g => g.First())
            .ToList();
    }

    // "Saturday 14 November 2026 · 10.30am & 12pm" → 2026-11-14. The year is
    // always published, so no soonest-future-year inference is needed.
    private static DateOnly? ParseDate(string text)
    {
        var m = Regex.Match(text, @"\b(\d{1,2})\s+([A-Za-z]+)\s+(\d{4})\b");
        if (!m.Success) return null;
        if (!int.TryParse(m.Groups[1].Value, out var day)) return null;
        if (!int.TryParse(m.Groups[3].Value, out var year)) return null;
        var month = ParseMonth(m.Groups[2].Value);
        if (month is null) return null;
        try { return new DateOnly(year, month.Value, day); }
        catch { return null; }
    }

    // Pulls every "10.30am" / "12pm" style token out of the date line — the
    // am/pm suffix is required, so the day number and year can't match.
    private static IReadOnlyList<TimeOnly> ParseTimes(string text)
    {
        var times = new List<TimeOnly>();
        foreach (Match m in Regex.Matches(text, @"\b(\d{1,2}(?:\.\d{2})?\s*(?:am|pm))\b", RegexOptions.IgnoreCase))
        {
            if (TextParsing.ParseClockTime(m.Groups[1].Value) is { } t && !times.Contains(t))
                times.Add(t);
        }
        return times;
    }

    // Fetches the concert's detail page for its "£N children" ticket tier —
    // the lowest real price and what we surface as Cost. Best-effort: any
    // failure (network, layout change) just leaves Cost null.
    private async Task<decimal?> TryGetChildPriceAsync(string url, CancellationToken ct)
    {
        try
        {
            var html = await _http.GetStringAsync(url, ct);
            var m = Regex.Match(html, @"(?:£|&#163;|&pound;)\s*(\d+(?:\.\d{1,2})?)\s*children",
                RegexOptions.IgnoreCase);
            if (m.Success && decimal.TryParse(m.Groups[1].Value, NumberStyles.Number,
                CultureInfo.InvariantCulture, out var v))
                return v;
            return null;
        }
        catch { return null; }
    }

    private static string SlugFromUrl(string href)
    {
        var trimmed = href.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        return lastSlash >= 0 ? trimmed[(lastSlash + 1)..] : trimmed;
    }

    private static int? ParseMonth(string s) => s.Trim().ToLowerInvariant() switch
    {
        "january" or "jan" => 1,
        "february" or "feb" => 2,
        "march" or "mar" => 3,
        "april" or "apr" => 4,
        "may" => 5,
        "june" or "jun" => 6,
        "july" or "jul" => 7,
        "august" or "aug" => 8,
        "september" or "sep" or "sept" => 9,
        "october" or "oct" => 10,
        "november" or "nov" => 11,
        "december" or "dec" => 12,
        _ => null,
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";
}
