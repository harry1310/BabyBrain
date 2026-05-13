using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using BabyBrain.Scrapers.Domain;
using BabyBrain.Scrapers.Shared;

namespace BabyBrain.Scrapers.WigmoreHall;

// Source: https://www.wigmore-hall.org.uk/tags/events-for-under-5s
// CloudFront-fronted but not behind a JS challenge, so plain HttpClient is
// enough — no Playwright. The listing renders each performance into a
// `.performance-listing__item` block with title, link, and a <time> element
// carrying the UTC ISO datetime. Ticket prices aren't in the listing markup
// (the booking button renders client-side from JS), so for price we follow
// each event's /whats-on/<id> page and read `.booking-fee-text`.
public sealed class WigmoreHallUnderFivesScraper : IScraper
{
    private const string ListingUrl = "https://www.wigmore-hall.org.uk/tags/events-for-under-5s";
    private const string Origin = "https://www.wigmore-hall.org.uk";
    private const string Venue = "Wigmore Hall";
    private const string Address = "36 Wigmore Street, London";
    private const string Postcode = "W1U 2BP";

    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    public string SourceId => "wigmore_hall_under_fives";
    public string Category => Categories.Concert;

    private readonly HttpClient _http;

    public WigmoreHallUnderFivesScraper(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<EventOccurrence>> ScrapeAsync(int horizonDays, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var horizonEnd = today.AddDays(horizonDays);
        var now = DateTimeOffset.UtcNow;
        var rows = new List<EventOccurrence>();

        var listingHtml = await _http.GetStringAsync(ListingUrl, ct);
        var listing = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(listingHtml), ct);

        foreach (var item in listing.QuerySelectorAll(".performance-listing__item"))
        {
            ct.ThrowIfCancellationRequested();
            var row = await BuildRowAsync(item, today, horizonEnd, now, ct);
            if (row is not null) rows.Add(row);
        }
        return rows;
    }

    private async Task<EventOccurrence?> BuildRowAsync(IElement item, DateOnly from, DateOnly to, DateTimeOffset now, CancellationToken ct)
    {
        var titleAnchor = item.QuerySelector("a.expand-interaction__action");
        var title = titleAnchor?.QuerySelector(".performance-title")?.TextContent?.Trim();
        var href = titleAnchor?.GetAttribute("href");
        if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(href)) return null;

        var timeEl = item.QuerySelector("time");
        var dtRaw = timeEl?.GetAttribute("datetime");
        if (string.IsNullOrEmpty(dtRaw)) return null;
        if (!DateTimeOffset.TryParse(dtRaw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dtUtc))
            return null;

        var localDt = TimeZoneInfo.ConvertTime(dtUtc, London);
        var date = DateOnly.FromDateTime(localDt.DateTime);
        if (date < from || date > to) return null;

        // Two copies of the description sit in the DOM — one shown on mobile,
        // one on desktop. Pick whichever's first.
        var description = item.QuerySelectorAll(".type-style-5")
            .Select(el => el.TextContent?.Trim())
            .FirstOrDefault(t => !string.IsNullOrEmpty(t) && t != timeEl?.TextContent?.Trim());

        var (minAge, maxAge) = TextParsing.ParseAgeRange(title + " " + (description ?? ""));

        var url = Origin + href;
        var eventId = ExtractEventId(href);

        var (isFree, cost) = await TryFetchPriceAsync(url, ct);

        return new EventOccurrence
        {
            ExternalKey = $"{SourceId}:{eventId}",
            Source = SourceId,
            Category = Category,
            SourceUrl = url,
            Date = date,
            StartTime = TimeOnly.FromDateTime(localDt.DateTime),
            EndTime = null,
            SessionName = title,
            SessionNotes = description,
            VenueName = Venue,
            VenueAddress = Address,
            Postcode = Postcode,
            MinAgeMonths = minAge,
            MaxAgeMonths = maxAge,
            TermTimeOnly = false,
            IsFree = isFree,
            Cost = cost,
            LastSeenAt = now,
        };
    }

    private async Task<(bool IsFree, decimal? Cost)> TryFetchPriceAsync(string url, CancellationToken ct)
    {
        try
        {
            var html = await _http.GetStringAsync(url, ct);
            var doc = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html), ct);
            var node = doc.QuerySelector(".booking-fee-text");
            if (node is null) return (false, null);
            // Strip <sub> subscripts (booking fees, e.g. "+£1 booking fee") so
            // they don't get mistaken for the ticket price.
            foreach (var sub in node.QuerySelectorAll("sub").ToList()) sub.Remove();
            return TextParsing.ParsePrice(node.TextContent);
        }
        catch
        {
            return (false, null);
        }
    }

    private static string ExtractEventId(string href)
    {
        // Listing hrefs are shaped /whats-on/202606031015 — the digits are a
        // stable date+time identifier we can use directly.
        var m = Regex.Match(href, @"/whats-on/(?<id>\d+)");
        return m.Success ? m.Groups["id"].Value : Regex.Replace(href, "[^a-zA-Z0-9]", "-");
    }
}
