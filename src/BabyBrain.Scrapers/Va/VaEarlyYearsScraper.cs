using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using BabyBrain.Scrapers.Domain;
using BabyBrain.Scrapers.Shared;
using Microsoft.Playwright;

namespace BabyBrain.Scrapers.Va;

// Source: https://www.vam.ac.uk/whatson?audience=early-years
// V&A's "What's on" listing embeds schema.org Event microdata on every card.
// The audience=early-years filter is client-side CSS — the rendered HTML
// actually contains every event, with early-years cards tagged via
// data-wo-audience. We filter to that subset in code.
//
// Multi-week series are detected via startDate/endDate spanning more than one
// day: we treat the cadence as weekly on startDate's day-of-week and emit one
// row per occurrence inside the horizon. Per-session times come from the start
// (time-of-day) and end (time-of-day, applied to every session) microdata.
// Single-day cards continue to emit one row.
public sealed class VaEarlyYearsScraper : IScraper
{
    private const string ListingUrl = "https://www.vam.ac.uk/whatson?audience=early-years";
    private const string Origin = "https://www.vam.ac.uk";

    public string SourceId => "va_early_years";
    public string Category => Categories.Museum;

    private readonly PlaywrightFetcher _fetcher;
    private readonly HttpClient _http;

    public VaEarlyYearsScraper(PlaywrightFetcher fetcher, HttpClient http)
    {
        _fetcher = fetcher;
        _http = http;
    }

    public async Task<IReadOnlyList<EventOccurrence>> ScrapeAsync(int horizonDays, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var horizonEnd = today.AddDays(horizonDays);
        var now = DateTimeOffset.UtcNow;
        var rows = new List<EventOccurrence>();

        // Use Attached, not Visible — the audience filter is client-side CSS
        // that hides non-matching cards. The DOM contains all events; we just
        // need them present, not painted.
        var html = await _fetcher.FetchRenderedHtmlAsync(
            ListingUrl,
            "li.b-event-teaser article[itemtype='http://schema.org/Event']",
            WaitForSelectorState.Attached,
            ct);
        var doc = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html), ct);

        foreach (var li in doc.QuerySelectorAll("li.b-event-teaser"))
        {
            var audience = li.GetAttribute("data-wo-audience") ?? "";
            // Tokens are space-separated. Must contain "early-years"; skip school-only programmes.
            var tokens = audience.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!tokens.Contains("early-years")) continue;
            if (tokens.Contains("schools") && !tokens.Contains("families")) continue;

            rows.AddRange(await BuildRowsAsync(li, today, horizonEnd, now, ct));
        }
        return rows;
    }

    private async Task<IReadOnlyList<EventOccurrence>> BuildRowsAsync(IElement li, DateOnly from, DateOnly to, DateTimeOffset now, CancellationToken ct)
    {
        var article = li.QuerySelector("article[itemtype='http://schema.org/Event']");
        if (article is null) return Array.Empty<EventOccurrence>();

        var name = article.QuerySelector("meta[itemprop='name']")?.GetAttribute("content")?.Trim() ?? "";
        var startRaw = article.QuerySelector("meta[itemprop='startDate']")?.GetAttribute("content")?.Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(startRaw)) return Array.Empty<EventOccurrence>();

        if (!TryParseDateTime(startRaw, out var start)) return Array.Empty<EventOccurrence>();
        // V&A's listing microdata is an hour out: it encodes each event's true
        // local clock time as a UTC instant of the same reading — e.g. an 11:30
        // session is published as the instant 11:30Z (tagged variously +0000 or
        // +0100). UtcDateTime recovers that instant = the real local time.
        // .DateTime keeps the offset-shifted wall clock and ConvertTime adds a
        // spurious BST hour — both land an hour late. The event detail pages
        // confirm the times this produces.
        var startLocal = start.UtcDateTime;
        var seriesFirst = DateOnly.FromDateTime(startLocal);
        var startTime = TimeOnly.FromDateTime(startLocal);

        var endRaw = article.QuerySelector("meta[itemprop='endDate']")?.GetAttribute("content")?.Trim();
        DateOnly seriesLast = seriesFirst;
        TimeOnly? sessionEndTime = null;
        if (!string.IsNullOrEmpty(endRaw) && TryParseDateTime(endRaw, out var end))
        {
            var endLocal = end.UtcDateTime; // see startLocal — V&A's feed runs an hour late
            seriesLast = DateOnly.FromDateTime(endLocal);
            // The schema.org endDate carries the *last session's* end clock-time,
            // which is the same slot every week — apply it to every occurrence.
            sessionEndTime = TimeOnly.FromDateTime(endLocal);
        }

        // Drop the card entirely if the whole series sits outside the horizon.
        if (seriesLast < from || seriesFirst > to) return Array.Empty<EventOccurrence>();

        var windowStart = seriesFirst < from ? from : seriesFirst;
        var windowEnd = seriesLast > to ? to : seriesLast;

        var description = article.QuerySelector("meta[itemprop='description']")?.GetAttribute("content")?.Trim();

        var href = article.QuerySelector("a.b-event-teaser__link")?.GetAttribute("href") ?? "";
        var url = href.StartsWith("http") ? href : Origin + href;
        var eventId = ExtractEventId(href);

        var isSeries = seriesFirst != seriesLast;

        // A series' real session dates aren't in the listing — it carries only a
        // first + last date. They ARE enumerated on the event's detail page, so
        // fetch it once (and reuse the HTML for the age fallback below).
        string? detailHtml = null;
        if (isSeries && !string.IsNullOrEmpty(url))
            detailHtml = await TryFetchAsync(url, ct);

        var (minAge, maxAge) = TextParsing.ParseAgeRange((name + " " + description) ?? name);

        // Some listing cards omit the description meta entirely (e.g. Rhythm
        // Makers n3zEzkz33J7), leaving nothing to parse age from. Fall back to
        // the detail page's <meta name="description">, which V&A populates more
        // reliably ("…for families with children aged 2-5").
        if (minAge is null && maxAge is null && !string.IsNullOrEmpty(url))
        {
            var detailForAge = detailHtml ?? await TryFetchAsync(url, ct);
            var detailDesc = detailForAge is null ? null : ExtractMetaDescription(detailForAge);
            if (!string.IsNullOrEmpty(detailDesc))
                (minAge, maxAge) = TextParsing.ParseAgeRange(name + " " + detailDesc);
        }

        var venue = ResolveVenue(li.GetAttribute("data-wo-venue"));
        var notes = string.IsNullOrEmpty(description) ? null : Truncate(description, 400);

        // One-off (start and end on the same calendar day) → a single row.
        // Series → the genuine session dates enumerated on the detail page. V&A
        // series run varied cadences (twice-weekly, fortnightly) that a weekly
        // guess gets wrong; if the detail page can't be read, fall back to that
        // guess on seriesFirst's day-of-week rather than dropping the event.
        IEnumerable<DateOnly> dates;
        if (!isSeries)
        {
            dates = new[] { seriesFirst }.Where(d => d >= windowStart && d <= windowEnd);
        }
        else
        {
            var sessionDates = detailHtml is null
                ? new List<DateOnly>()
                : ExtractSessionDates(detailHtml, seriesFirst, seriesLast);
            dates = sessionDates.Count > 0
                ? sessionDates.Where(d => d >= windowStart && d <= windowEnd)
                : TextParsing.WeeklyDatesInWindow(seriesFirst.DayOfWeek, windowStart, windowEnd);
        }

        var rows = new List<EventOccurrence>();
        foreach (var date in dates)
        {
            rows.Add(new EventOccurrence
            {
                ExternalKey = $"{SourceId}:{eventId}:{date:yyyy-MM-dd}:{startTime:HHmm}",
                Source = SourceId,
                Category = Category,
                SourceUrl = url,
                Date = date,
                StartTime = startTime,
                EndTime = sessionEndTime,
                SessionName = name,
                SessionNotes = notes,
                VenueName = venue.Name,
                VenueAddress = venue.Address,
                Postcode = venue.Postcode,
                MinAgeMonths = minAge,
                MaxAgeMonths = maxAge,
                TermTimeOnly = false,
                IsFree = true,
                LastSeenAt = now,
            });
        }
        return rows;
    }

    private async Task<string?> TryFetchAsync(string url, CancellationToken ct)
    {
        try { return await _http.GetStringAsync(url, ct); }
        catch { return null; }
    }

    // The detail page's <meta name="description"> — a short marketing string,
    // used as an age-range fallback. Regex rather than AngleSharp: it's one
    // element and we may already be regex-scanning this HTML for session dates.
    private static string? ExtractMetaDescription(string html)
    {
        var m = Regex.Match(html, @"<meta\s+name=""description""\s+content=""([^""]*)""", RegexOptions.IgnoreCase);
        return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value) : null;
    }

    // V&A detail pages enumerate each remaining session as "Friday, 22 May"
    // (weekday, day, month — no year). Pulling those out gives the true session
    // list whatever the cadence. The year is inferred from the series' known
    // [first, last] span; matches outside that span (e.g. a related event) are
    // rejected. Returns distinct dates, ascending.
    private static List<DateOnly> ExtractSessionDates(string html, DateOnly seriesFirst, DateOnly seriesLast)
    {
        var dates = new SortedSet<DateOnly>();
        foreach (Match m in Regex.Matches(html,
            @"(?:Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday),\s+(\d{1,2})\s+([A-Za-z]+)"))
        {
            if (!int.TryParse(m.Groups[1].Value, out var day)) continue;
            if (ParseMonth(m.Groups[2].Value) is not int month) continue;
            foreach (var year in new[] { seriesFirst.Year, seriesLast.Year })
            {
                DateOnly d;
                try { d = new DateOnly(year, month, day); }
                catch (ArgumentOutOfRangeException) { continue; }
                if (d >= seriesFirst && d <= seriesLast) { dates.Add(d); break; }
            }
        }
        return dates.ToList();
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

    private record Venue(string Name, string Address, string Postcode);

    // V&A operates three public sites. data-wo-venue on each event card
    // identifies which one; the microdata `address` field is hardcoded to
    // South Kensington and can't be trusted.
    private static Venue ResolveVenue(string? slug) => slug switch
    {
        "young" => new("Young V&A", "Cambridge Heath Road, London", "E2 9PA"),
        "east-storehouse" => new("V&A East Storehouse", "Olympic Park, Stratford, London", "E20 3BB"),
        _ => new("V&A South Kensington", "Cromwell Road, London", "SW7 2RL"),
    };

    // href shape: /event/<id>/<slug> — the id is the V&A internal event identifier.
    private static string ExtractEventId(string href)
    {
        var m = Regex.Match(href, @"/event/(?<id>[^/?#]+)");
        return m.Success ? m.Groups["id"].Value : Slug(href);
    }

    private static bool TryParseDateTime(string raw, out DateTimeOffset value)
    {
        // Format: "2026-05-01 12:00:00 +0100"
        return DateTimeOffset.TryParseExact(
            raw,
            "yyyy-MM-dd HH:mm:ss zzz",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out value);
    }

    private static string Slug(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";
}
