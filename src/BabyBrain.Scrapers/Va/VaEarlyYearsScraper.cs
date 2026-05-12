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
// v1 caveat: each card's startDate/endDate is the *first* and *last* occurrence
// of a multi-week series; individual session dates live in the free-text
// description. We emit one row per event at the startDate and leave per-session
// expansion for v2.
public sealed class VaEarlyYearsScraper : IScraper
{
    private const string ListingUrl = "https://www.vam.ac.uk/whatson?audience=early-years";
    private const string Origin = "https://www.vam.ac.uk";

    public string SourceId => "va_early_years";
    public string Category => Categories.Museum;

    private readonly PlaywrightFetcher _fetcher;

    public VaEarlyYearsScraper(PlaywrightFetcher fetcher) => _fetcher = fetcher;

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

            var row = BuildRow(li, today, horizonEnd, now);
            if (row is not null) rows.Add(row);
        }
        return rows;
    }

    private EventOccurrence? BuildRow(IElement li, DateOnly from, DateOnly to, DateTimeOffset now)
    {
        var article = li.QuerySelector("article[itemtype='http://schema.org/Event']");
        if (article is null) return null;

        var name = article.QuerySelector("meta[itemprop='name']")?.GetAttribute("content")?.Trim() ?? "";
        var startRaw = article.QuerySelector("meta[itemprop='startDate']")?.GetAttribute("content")?.Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(startRaw)) return null;

        if (!TryParseDateTime(startRaw, out var start)) return null;
        var date = DateOnly.FromDateTime(start.LocalDateTime);
        if (date < from || date > to) return null;

        var endRaw = article.QuerySelector("meta[itemprop='endDate']")?.GetAttribute("content")?.Trim();
        TimeOnly? endTime = null;
        if (!string.IsNullOrEmpty(endRaw) && TryParseDateTime(endRaw, out var end))
        {
            // Only use endTime when end is on the same day as start — otherwise
            // it's the last day of a series, not the close of one session.
            if (DateOnly.FromDateTime(end.LocalDateTime) == date)
                endTime = TimeOnly.FromDateTime(end.LocalDateTime);
        }

        var description = article.QuerySelector("meta[itemprop='description']")?.GetAttribute("content")?.Trim();
        var (minAge, maxAge) = TextParsing.ParseAgeRange((name + " " + description) ?? name);

        var href = article.QuerySelector("a.b-event-teaser__link")?.GetAttribute("href") ?? "";
        var url = href.StartsWith("http") ? href : Origin + href;
        var eventId = ExtractEventId(href);

        var venue = ResolveVenue(li.GetAttribute("data-wo-venue"));

        return new EventOccurrence
        {
            ExternalKey = $"{SourceId}:{eventId}:{date:yyyy-MM-dd}:{TimeOnly.FromDateTime(start.LocalDateTime):HHmm}",
            Source = SourceId,
            Category = Category,
            SourceUrl = url,
            Date = date,
            StartTime = TimeOnly.FromDateTime(start.LocalDateTime),
            EndTime = endTime,
            SessionName = name,
            SessionNotes = string.IsNullOrEmpty(description) ? null : Truncate(description, 400),
            VenueName = venue.Name,
            VenueAddress = venue.Address,
            Postcode = venue.Postcode,
            MinAgeMonths = minAge,
            MaxAgeMonths = maxAge,
            TermTimeOnly = false,
            LastSeenAt = now,
        };
    }

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
