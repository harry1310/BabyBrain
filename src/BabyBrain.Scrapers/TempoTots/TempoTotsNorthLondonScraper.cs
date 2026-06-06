using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp;
using BabyBrain.Scrapers.Domain;
using BabyBrain.Scrapers.Shared;

namespace BabyBrain.Scrapers.TempoTots;

// Source: https://www.tempototsnorthlondon.co.uk/
// A single weekly drop-in music & sensory class, not a listing. The site is a
// Wix build (hashed, unstable class names) so we can't lean on CSS structure —
// but the few facts we need sit in plain text we can anchor on:
//   day    → "Drop-in music Monday afternoons"
//   time   → "Book for the Farm 3.30pm"
//   age    → "children from 0-4 years"
//   price  → "Classes are priced at £8/child…"
// Venue/address are fixed (one location), so they're constants; we still fetch
// the page and require the venue name to be present, so a site rewrite fails
// loudly (→ a claude-fix issue) rather than silently emitting stale rows.
//
// Fetched with a plain browser-UA HttpClient, with a few retries: the Wix edge
// is flaky and intermittently 404s (it has erred for both the VPS and a
// residential browser at times). ScraperAPI isn't worth a paid credit for one
// recurring weekly class, so we just retry the fetch a handful of times and let
// the source fail loudly (→ a claude-fix issue) if every attempt fails.
//
// Recurrence is implicit weekly on the named day; we materialise one row per
// occurrence across the horizon, the same model as the Camden / Postal Museum
// timetables.
public sealed class TempoTotsNorthLondonScraper : IScraper
{
    private const string PageUrl = "https://www.tempototsnorthlondon.co.uk/";
    private const string Venue = "Kentish Town City Farm";
    private const string Address = "1 Cressfield Close, Kentish Town, London";
    private const string Postcode = "NW5 4BN";
    private const string SessionTitle = "Tempo Tots music & sensory class";

    public string SourceId => "tempo_tots_north_london";
    public string Category => Categories.Class;

    // Wix flakiness mitigation: retry the page fetch a few times with a short
    // pause before giving up.
    private const int FetchAttempts = 4;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly HttpClient _http;

    public TempoTotsNorthLondonScraper(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<EventOccurrence>> ScrapeAsync(int horizonDays, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var horizonEnd = today.AddDays(horizonDays);
        var now = DateTimeOffset.UtcNow;

        var html = await FetchPageWithRetriesAsync(ct);
        var doc = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html), ct);

        // One run-together string of the page's visible text. AngleSharp has
        // already decoded entities (so "£8" reads as £8); collapse whitespace so
        // our anchors aren't defeated by Wix's generous spacing.
        var text = Regex.Replace(doc.Body?.TextContent ?? "", @"\s+", " ").Trim();

        // Liveness guard: if the venue name is gone the page has been rebuilt and
        // our anchors can't be trusted — fail so the source gets re-checked.
        if (!text.Contains(Venue, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Tempo Tots: venue anchor '{Venue}' not found — page structure may have changed.");

        var day = ParseDay(text)
            ?? throw new InvalidOperationException("Tempo Tots: could not find the class day on the page.");
        var start = ParseFarmTime(text)
            ?? throw new InvalidOperationException("Tempo Tots: could not find the class time on the page.");

        var (minAge, maxAge) = ParseAge(text);
        var cost = ParsePrice(text);
        var notes = BuildNotes(text);

        var rows = new List<EventOccurrence>();
        foreach (var date in TextParsing.WeeklyDatesInWindow(day, today, horizonEnd))
        {
            rows.Add(new EventOccurrence
            {
                ExternalKey = $"{SourceId}:{date:yyyy-MM-dd}:{start:HHmm}",
                Source = SourceId,
                Category = Category,
                SourceUrl = PageUrl,
                Date = date,
                StartTime = start,
                EndTime = null,
                SessionName = SessionTitle,
                SessionNotes = notes,
                VenueName = Venue,
                VenueAddress = Address,
                Postcode = Postcode,
                MinAgeMonths = minAge,
                MaxAgeMonths = maxAge,
                TermTimeOnly = false,
                IsFree = false,
                Cost = cost,
                LastSeenAt = now,
            });
        }
        return rows;
    }

    // Fetch the page, retrying on any HTTP failure (Wix intermittently 404s).
    // GetStringAsync throws HttpRequestException on a non-success status.
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
            // Retry HTTP errors (Wix 404s) and request timeouts
            // (TaskCanceledException), but not a genuine cancellation.
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
            $"Tempo Tots: page fetch failed after {FetchAttempts} attempts.", last);
    }

    // The class day is written as "Drop-in music Monday afternoons". Anchor on
    // "music <weekday>" so we don't grab a stray weekday from elsewhere.
    private static DayOfWeek? ParseDay(string text)
    {
        var m = Regex.Match(text,
            @"music\s+(Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday)",
            RegexOptions.IgnoreCase);
        return m.Success ? TextParsing.ParseDayOfWeek(m.Groups[1].Value) : null;
    }

    // The in-person session time sits on the booking button "Book for the Farm
    // 3.30pm". Anchoring on "Farm" keeps us off any livestream-only time.
    private static TimeOnly? ParseFarmTime(string text)
    {
        var m = Regex.Match(text, @"Farm\s+(\d{1,2})[.:](\d{2})\s*([ap]m)", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var hour = int.Parse(m.Groups[1].Value);
        var minute = int.Parse(m.Groups[2].Value);
        var pm = m.Groups[3].Value.Equals("pm", StringComparison.OrdinalIgnoreCase);
        if (pm && hour != 12) hour += 12;
        if (!pm && hour == 12) hour = 0;
        if (hour > 23 || minute > 59) return null;
        return new TimeOnly(hour, minute);
    }

    // "children from 0-4 years" → 0–48 months. TextParsing.ParseAgeRange doesn't
    // recognise the bare "N-M years" wording, so handle it here.
    private static (int? min, int? max) ParseAge(string text)
    {
        var m = Regex.Match(text, @"(\d{1,2})\s*[–-]\s*(\d{1,2})\s*years?\b", RegexOptions.IgnoreCase);
        if (m.Success)
            return (int.Parse(m.Groups[1].Value) * 12, int.Parse(m.Groups[2].Value) * 12);
        return TextParsing.ParseAgeRange(text);
    }

    // Headline child price. Not TextParsing.ParsePrice: "Babies under 6 months go
    // FREE with an older sibling" would trip its free-detection — that's a
    // concession, not a free class.
    private static decimal? ParsePrice(string text)
    {
        var m = Regex.Match(text, @"£\s*(\d+(?:\.\d{1,2})?)\s*/?\s*child", RegexOptions.IgnoreCase);
        if (!m.Success) m = Regex.Match(text, @"priced at\s*£\s*(\d+(?:\.\d{1,2})?)", RegexOptions.IgnoreCase);
        return m.Success && decimal.TryParse(m.Groups[1].Value, NumberStyles.Number,
            CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static string? BuildNotes(string text)
    {
        var parts = new List<string> { "Drop-in music & sensory class." };
        if (Regex.IsMatch(text, @"under 6 months go FREE", RegexOptions.IgnoreCase))
            parts.Add("Babies under 6 months go free with an older sibling.");
        parts.Add("Booking advised — places are limited.");
        return string.Join(" ", parts);
    }
}
