using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using BabyBrain.Scrapers.Domain;
using BabyBrain.Scrapers.Shared;

namespace BabyBrain.Scrapers.Barbican;

// Source: https://www.barbican.org.uk/whats-on/series/parent-and-baby-screenings
// Drupal site, server-rendered. The listing page exposes each screening as an
// <article class="listing--event"> with .listing-title, an <a> link, and a
// .listing-date.date-range containing one or two <time datetime="..."> tags.
// We follow the detail page only for the standard ticket price (Enta booking
// widget loads showtimes via JS, so the static HTML's <time> tags are the
// authoritative session list).
public sealed class BarbicanParentAndBabyScraper : IScraper
{
    private const string ListingUrl = "https://www.barbican.org.uk/whats-on/series/parent-and-baby-screenings";
    private const string Origin = "https://www.barbican.org.uk";
    private const string Venue = "Barbican Centre";
    private const string Address = "Silk Street, London";
    private const string Postcode = "EC2Y 8DS";

    // "Parent & Baby Screening" series at the Barbican is explicitly for
    // pre-walking babies; the venue's own policy caps at ~12 months.
    private const int DefaultMinAgeMonths = 0;
    private const int DefaultMaxAgeMonths = 12;

    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    public string SourceId => "barbican_parent_and_baby";
    public string Category => Categories.Cinema;

    private readonly HttpClient _http;

    public BarbicanParentAndBabyScraper(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<EventOccurrence>> ScrapeAsync(int horizonDays, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var horizonEnd = today.AddDays(horizonDays);
        var now = DateTimeOffset.UtcNow;
        var rows = new List<EventOccurrence>();

        var listingHtml = await _http.GetStringAsync(ListingUrl, ct);
        var listing = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(listingHtml), ct);

        foreach (var card in listing.QuerySelectorAll("article.listing--event"))
        {
            ct.ThrowIfCancellationRequested();
            rows.AddRange(await BuildRowsAsync(card, today, horizonEnd, now, ct));
        }
        return rows;
    }

    private async Task<IEnumerable<EventOccurrence>> BuildRowsAsync(IElement card, DateOnly from, DateOnly to, DateTimeOffset now, CancellationToken ct)
    {
        var titleEl = card.QuerySelector("h2.listing-title");
        var title = titleEl?.TextContent?.Trim();
        if (string.IsNullOrEmpty(title)) return Array.Empty<EventOccurrence>();

        var linkEl = card.QuerySelector("a.search-listing__link");
        var href = linkEl?.GetAttribute("href");
        if (string.IsNullOrEmpty(href)) return Array.Empty<EventOccurrence>();
        var url = href.StartsWith("http") ? href : Origin + href;

        // The listing card's <time> tags ARE the session list (start + end of
        // the multi-screening run, both at the same clock time). Dedupe by
        // local-date so a same-day repeat doesn't produce two rows.
        var sessions = card.QuerySelectorAll("p.listing-date.date-range time[datetime]")
            .Select(t => t.GetAttribute("datetime"))
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => TryParseUtc(s!))
            .Where(dt => dt is not null)
            .Select(dt => TimeZoneInfo.ConvertTime(dt!.Value, London))
            .GroupBy(localDt => DateOnly.FromDateTime(localDt.DateTime))
            .Select(g => g.First())
            .ToList();
        if (sessions.Count == 0) return Array.Empty<EventOccurrence>();

        var description = card.QuerySelector(".search-listing__intro")?.TextContent?.Trim();
        var notes = string.IsNullOrEmpty(description) ? null : Truncate(description, 400);

        var eventId = ExtractEventId(href);
        var cost = await TryFetchPriceAsync(url, ct);

        var result = new List<EventOccurrence>();
        foreach (var localDt in sessions)
        {
            var date = DateOnly.FromDateTime(localDt.DateTime);
            if (date < from || date > to) continue;
            var startTime = TimeOnly.FromDateTime(localDt.DateTime);
            result.Add(new EventOccurrence
            {
                ExternalKey = $"{SourceId}:{eventId}:{date:yyyy-MM-dd}:{startTime:HHmm}",
                Source = SourceId,
                Category = Category,
                SourceUrl = url,
                Date = date,
                StartTime = startTime,
                EndTime = null,
                SessionName = title,
                SessionNotes = notes,
                VenueName = Venue,
                VenueAddress = Address,
                Postcode = Postcode,
                MinAgeMonths = DefaultMinAgeMonths,
                MaxAgeMonths = DefaultMaxAgeMonths,
                TermTimeOnly = false,
                IsFree = false,
                Cost = cost,
                LastSeenAt = now,
            });
        }
        return result;
    }

    private async Task<decimal?> TryFetchPriceAsync(string url, CancellationToken ct)
    {
        try
        {
            var html = await _http.GetStringAsync(url, ct);
            var doc = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html), ct);
            // The price block is keyed on .ticket-prices; values live in
            // .accordion-item__value (e.g. "£6"). Booking fees sit in a
            // sibling .ticket-prices__booking-fee tooltip and are skipped.
            var prices = doc.QuerySelector(".ticket-prices");
            if (prices is null) return null;
            var values = prices.QuerySelectorAll(".accordion-item__value");
            if (values.Length == 0) return null;
            var combined = string.Join(" ", values.Select(v => v.TextContent?.Trim() ?? ""));
            var (_, cost) = TextParsing.ParsePrice(combined);
            return cost;
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset? TryParseUtc(string raw)
    {
        // datetime attrs on the listing are ISO-8601 with a trailing Z, e.g.
        // "2026-05-11T10:15:00Z".
        return DateTimeOffset.TryParse(
            raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var dt) ? dt : null;
    }

    private static string ExtractEventId(string href)
    {
        // Hrefs are shaped /whats-on/<year>/event/<slug> — the slug is the
        // stable identifier we want.
        var m = Regex.Match(href, @"/event/(?<id>[^/?#]+)");
        return m.Success ? m.Groups["id"].Value : Regex.Replace(href, "[^a-zA-Z0-9]+", "-").Trim('-');
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";
}
