using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using BabyBrain.Scrapers.Domain;
using BabyBrain.Scrapers.Shared;

namespace BabyBrain.Scrapers.WildLondon;

// Source: https://www.wildlondon.org.uk/family-learning-events
// London Wildlife Trust's free family nature events (pond dipping, minibeast
// hunts, nature clubs). The site is a Drupal build, three levels deep:
//   hub      /family-learning-events          -> ~5 nature-reserve pages
//   reserve  /family-learning-events/<slug>   -> that reserve's event cards
//   event    /events/<yyyy-MM-dd>-<slug>      -> the dated event, full detail
// We walk hub -> reserve -> event and emit one row per event. Each event URL
// begins with the event date, so out-of-window events are filtered out before
// we pay for a detail fetch.
public sealed class WildLondonFamilyLearningScraper : IScraper
{
    private const string HubUrl = "https://www.wildlondon.org.uk/family-learning-events";
    private const string Origin = "https://www.wildlondon.org.uk";

    // Used only if an event page omits the time-value field. Such rows are
    // stamped TimeApproximate = true so the UI flags the placeholder.
    private static readonly TimeOnly PlaceholderStart = new(10, 0);

    public string SourceId => "wild_london_family_learning";
    public string Category => Categories.Outdoors;

    private readonly PlaywrightFetcher _fetcher;

    public WildLondonFamilyLearningScraper(PlaywrightFetcher fetcher) => _fetcher = fetcher;

    public async Task<IReadOnlyList<EventOccurrence>> ScrapeAsync(int horizonDays, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var horizonEnd = today.AddDays(horizonDays);
        var now = DateTimeOffset.UtcNow;
        var rows = new List<EventOccurrence>();

        // Level 0: hub -> reserve page URLs.
        var hubHtml = await _fetcher.FetchRenderedHtmlAsync(HubUrl, "a[href*='/family-learning-events/']", ct: ct);
        var hub = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(hubHtml), ct);
        var reserveUrls = ExtractReserveUrls(hub).ToList();

        // Level 1: each reserve page -> event detail links. The slug carries the
        // date, so anything outside the horizon is dropped before a detail fetch.
        var events = new Dictionary<string, EventLink>(StringComparer.OrdinalIgnoreCase);
        foreach (var reserveUrl in reserveUrls)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var reserveHtml = await _fetcher.FetchRenderedHtmlAsync(reserveUrl, "article.node--type-event", ct: ct);
                var reserve = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(reserveHtml), ct);
                foreach (var link in ExtractEventLinks(reserve))
                {
                    var slugDate = DateFromSlug(link.Url);
                    if (slugDate is { } d && (d < today || d > horizonEnd)) continue;
                    events[link.Url] = link;
                }
            }
            catch (Exception ex)
            {
                // A reserve with no events 30s-times-out on the wait selector;
                // log it so we know which reserve we skipped, then carry on.
                Console.WriteLine($"  skipped reserve {reserveUrl}: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
            }
        }

        // Level 2: each event detail page -> one row.
        foreach (var link in events.Values)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var html = await _fetcher.FetchRenderedHtmlAsync(link.Url, "time[itemprop='startDate']", ct: ct);
                var doc = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html), ct);
                var row = BuildOccurrence(doc, link, now);
                if (row is not null && row.Date >= today && row.Date <= horizonEnd)
                    rows.Add(row);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  skipped event {link.Url}: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
            }
        }
        return rows;
    }

    private record EventLink(string Url, string Title);

    private static IEnumerable<string> ExtractReserveUrls(IDocument hub)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in hub.QuerySelectorAll("a[href*='/family-learning-events/']"))
        {
            var href = a.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href)) continue;
            var abs = Absolute(href);
            // Keep only /family-learning-events/<single-slug> — the reserve
            // pages — not the hub itself or anything deeper.
            if (Regex.IsMatch(abs, @"/family-learning-events/[^/?#]+/?$") && seen.Add(abs))
                yield return abs;
        }
    }

    private static IEnumerable<EventLink> ExtractEventLinks(IDocument reserve)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in reserve.QuerySelectorAll("article.node--type-event"))
        {
            var titleLink = card.QuerySelector("h3.card__title a");
            var href = titleLink?.GetAttribute("href")
                       ?? card.QuerySelector("a[href*='/events/']")?.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href)) continue;

            var abs = Absolute(href);
            if (!seen.Add(abs)) continue;
            var title = titleLink?.TextContent.Trim() ?? "";
            yield return new EventLink(abs, title);
        }
    }

    private EventOccurrence? BuildOccurrence(IDocument doc, EventLink link, DateTimeOffset now)
    {
        // Date — the event-date field's <time> carries an ISO timestamp; fall
        // back to the date baked into the URL slug.
        var dateIso = doc.QuerySelector(".field--name-field-event-date time[datetime]")?.GetAttribute("datetime");
        var date = ParseIsoDate(dateIso) ?? DateFromSlug(link.Url);
        if (date is null) return null;

        var title = link.Title.Length > 0
            ? link.Title
            : doc.QuerySelector("h1")?.TextContent.Trim() ?? "";
        if (title.Length == 0) return null;

        // Time — the "1:00pm - 3:00pm" range in the time-value field. Absent on
        // some events; those fall back to a placeholder flagged approximate.
        var timeText = doc.QuerySelector(".field--name-field-event-time-value .field__item")?.TextContent;
        var (start, end) = ParseTimeRange(timeText);

        var summary = doc.QuerySelector(".field--name-field-event-summary")?.TextContent.Trim() ?? "";
        var about = StripLabel(CollapseWs(doc.QuerySelector(".field--name-field-event-about")?.TextContent ?? ""));
        var notes = summary.Length > 0 ? summary
                  : about.Length > 0 ? Truncate(about, 400)
                  : null;

        // Venue — the reserve reference block carries name + schema.org address.
        var reserveBlock = doc.QuerySelector(".field--name-field-event-reserve");
        var venue = Clean(reserveBlock?.QuerySelector("[itemprop='name']")?.TextContent);
        var street = Clean(reserveBlock?.QuerySelector("[itemprop='streetAddress']")?.TextContent);
        var locality = Clean(reserveBlock?.QuerySelector("[itemprop='addressLocality']")?.TextContent);
        var postcode = Clean(reserveBlock?.QuerySelector("[itemprop='postalCode']")?.TextContent);
        var address = string.Join(", ", new[] { street, locality }.Where(s => s.Length > 0));

        var (minAge, maxAge) = TextParsing.ParseAgeRange($"{title} {summary} {about}");
        var (isFree, cost) = TextParsing.ParsePrice($"{summary} {about}");

        var slug = Regex.Match(link.Url, @"/events/([^/?#]+)").Groups[1].Value;

        return new EventOccurrence
        {
            ExternalKey = $"{SourceId}:{(slug.Length > 0 ? slug : Slug(link.Url))}",
            Source = SourceId,
            Category = Category,
            SourceUrl = link.Url,
            Date = date.Value,
            StartTime = start ?? PlaceholderStart,
            EndTime = end,
            TimeApproximate = start is null,
            SessionName = title,
            SessionNotes = notes,
            VenueName = venue.Length > 0 ? venue : "London Wildlife Trust",
            VenueAddress = address.Length > 0 ? address : null,
            Postcode = postcode.Length > 0 ? postcode : null,
            MinAgeMonths = minAge,
            MaxAgeMonths = maxAge,
            TermTimeOnly = false,
            IsFree = isFree,
            Cost = cost,
            LastSeenAt = now,
        };
    }

    // Event URLs look like /events/2026-05-31-<slug>; pull the leading date.
    private static DateOnly? DateFromSlug(string url)
    {
        var m = Regex.Match(url, @"/events/(\d{4})-(\d{2})-(\d{2})-");
        if (!m.Success) return null;
        try
        {
            return new DateOnly(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                                int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                                int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static DateOnly? ParseIsoDate(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return null;
        return DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto)
            ? DateOnly.FromDateTime(dto.UtcDateTime)
            : null;
    }

    // "1:00pm - 3:00pm" / "10.30am – 12pm" / "2pm" (single, no end).
    private static (TimeOnly? Start, TimeOnly? End) ParseTimeRange(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, null);
        var parts = Regex.Split(raw.Trim(), @"\s*[–—-]\s*");
        var start = TextParsing.ParseClockTime(parts[0]);
        TimeOnly? end = parts.Length > 1 ? TextParsing.ParseClockTime(parts[1]) : null;
        return (start, end);
    }

    private static string Absolute(string href) =>
        href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? href
            : Origin + (href.StartsWith('/') ? href : "/" + href);

    // Drupal field text often ends with a trailing comma/whitespace ("London,").
    private static string Clean(string? s) => s is null ? "" : s.Trim().Trim(',').Trim();

    private static string CollapseWs(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    // The about field's text content is prefixed with its "About the event"
    // heading label — drop it so notes read cleanly.
    private static string StripLabel(string s) =>
        Regex.Replace(s, @"^About the event\s*", "", RegexOptions.IgnoreCase);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";

    private static string Slug(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
}
