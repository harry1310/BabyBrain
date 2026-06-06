using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using BabyBrain.Scrapers.Domain;
using BabyBrain.Scrapers.Shared;
using Microsoft.Extensions.Logging;

namespace BabyBrain.Scrapers.Southbank;

// Source: https://www.southbankcentre.co.uk/visit-us/families/#upcoming-events
// The families hub lists curated family events in a carousel under
// #upcoming-events. Each .c-event-card already carries everything we need:
// title, date range, listing description, venue (hall) and price — so the
// hub alone drives every row.
//
// NOTE (2026 redesign): Southbank's site rebuild removed the per-performance
// time list from event detail pages — detail pages now show only a date
// *range*. The exact session times survive only inside the Tessitura
// ticketing system, which sits behind a Queue-it waiting room; we deliberately
// do not scrape that. So when the hub card gives a date but no time, we emit a
// placeholder start time (PlaceholderStart) and set TimeApproximate = true so
// the UI flags it. When the card *does* carry a time (e.g. "Sat 23 May 2026,
// 11am") we use it and leave TimeApproximate false.
//
// FETCH STRATEGY (issues #15 / #17 / #20): the hub and detail pages both
// server-side render when given a browser-shaped User-Agent — neither needs
// a JS engine. But Cloudflare blocks every request from the Hetzner VPS IP
// regardless of User-Agent or TLS shape; PR #19's curl-from-prod still got
// a "Just a moment..." challenge page. So we route both hub and detail
// through ScraperAPI's residential-proxy endpoint, which solves the CF
// challenge for us and returns clean SSR'd HTML.
//
// AGE FILTER (issues #12 / #13): the families carousel is "family events" not
// "under-5s events", so 5+ / 6+ shows (Blizzard, Play Along: Virtual
// Orchestra) leak in. The card markup carries no age signal, so for any card
// whose title/summary doesn't yield an age band, we fetch the detail page
// and read the "Age guidance" item under .c-event-need-to-know. If that
// resolves to a minimum age at or above UnderFiveCutoffMonths, we drop the
// event entirely.
public sealed class SouthbankCentreScraper : IScraper
{
    private const string HubUrl = "https://www.southbankcentre.co.uk/visit-us/families/";
    private const string Address = "Belvedere Road, London";
    private const string Postcode = "SE1 8XX"; // Southbank Centre, Belvedere Road

    // BabyBrain covers under-5s. Mirrors the British Museum scraper.
    private const int UnderFiveCutoffMonths = 60;

    // Used when the hub card gives a date but no time. Southbank no longer
    // publishes exact session times on the public site; this is an admitted
    // guess. Rows built with it carry TimeApproximate = true so the UI flags it.
    private static readonly TimeOnly PlaceholderStart = new(10, 0);

    public string SourceId => "southbank_centre_families";
    public string Category => Categories.Concert;

    private readonly IContentFetcher _fetcher;
    private readonly ILogger<SouthbankCentreScraper> _logger;

    public SouthbankCentreScraper(IContentFetcher fetcher, ILogger<SouthbankCentreScraper> logger)
    {
        _fetcher = fetcher;
        _logger = logger;
    }

    public async Task<IReadOnlyList<EventOccurrence>> ScrapeAsync(int horizonDays, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var horizonEnd = today.AddDays(horizonDays);
        var now = DateTimeOffset.UtcNow;
        var rows = new List<EventOccurrence>();

        var hubHtml = await _fetcher.FetchAsync(SourceId, HubUrl, CacheTtl.Listing, ct: ct);
        var hub = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(hubHtml), ct);

        foreach (var card in ExtractCards(hub))
        {
            ct.ThrowIfCancellationRequested();

            var (minAge, maxAge) = await ResolveAgeRangeAsync(card, ct);
            if (minAge is int min && min >= UnderFiveCutoffMonths)
            {
                _logger.LogInformation(
                    "Southbank: skipping '{Title}' as school-age (min {MinAge}mo from {Url})",
                    card.Title, min, card.Url);
                continue;
            }

            rows.AddRange(BuildOccurrences(card, minAge, maxAge, today, horizonEnd, now));
        }
        return rows;
    }

    // Decides the event's age band: first from card text (cheap), then —
    // only if the card carries no age signal at all — from the detail page's
    // "Age guidance" section. A detail-fetch failure leaves the row with no
    // age rather than dropping it; that mirrors the prior leaky-but-present
    // behaviour and avoids killing the row on a transient render timeout.
    private async Task<(int? min, int? max)> ResolveAgeRangeAsync(Card card, CancellationToken ct)
    {
        // Cheapest signal first: the card's own text needs no fetch.
        var (m, x) = TextParsing.ParseAgeRange(card.Title + " " + card.Summary);
        if (m is not null || x is not null) return (m, x);

        // Otherwise read the detail page's "Age guidance". The fetch is cached
        // (detail TTL) by the content fetcher, so on most daily runs this costs
        // no credits — the event's age doesn't change between fetches.
        try
        {
            var detailHtml = await _fetcher.FetchAsync(SourceId, card.Url, CacheTtl.Detail, ct: ct);
            var detail = await BrowsingContext.New(Configuration.Default)
                .OpenAsync(req => req.Content(detailHtml), ct);

            foreach (var item in detail.QuerySelectorAll(".c-event-need-to-know__item"))
            {
                var title = item.QuerySelector(".c-event-need-to-know__title")?.TextContent.Trim();
                if (!string.Equals(title, "Age guidance", StringComparison.OrdinalIgnoreCase)) continue;
                var info = item.QuerySelector(".c-event-need-to-know__info")?.TextContent.Trim();
                if (string.IsNullOrEmpty(info)) break;
                return TextParsing.ParseAgeRange(info);
            }
        }
        catch (ScraperApiCreditsExhaustedException) { throw; } // billing state — block the run, don't degrade silently
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Southbank: detail-page age lookup failed for {Url}; emitting without age", card.Url);
        }
        return (null, null);
    }

    private record Card(string Title, string Summary, string Url, string Venue,
                         string DateRangeText, bool IsFree, decimal? Cost);

    private static IEnumerable<Card> ExtractCards(IDocument hub)
    {
        var anchor = hub.QuerySelector("#upcoming-events");
        var section = anchor?.Closest("section");
        if (section is null) yield break;

        // The carousel can clone slides for its scroll behaviour, so the same
        // .c-event-card may appear more than once — dedupe on the cover URL.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var card in section.QuerySelectorAll(".c-event-card"))
        {
            var href = card.QuerySelector("a.c-event-card__cover-link")?.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href) || !seen.Add(href)) continue;

            var title = card.QuerySelector(".c-event-card__title")?.TextContent.Trim() ?? "";
            var dateText = card.QuerySelector(".c-event-card__daterange")?.TextContent.Trim() ?? "";
            if (title.Length == 0 || dateText.Length == 0) continue;

            var summary = card.QuerySelector(".c-event-card__listing-details")?.TextContent.Trim() ?? "";
            var venue = card.QuerySelector(".c-event-card__location")?.TextContent.Trim();
            if (string.IsNullOrEmpty(venue)) venue = "Southbank Centre";
            var (isFree, cost) = TextParsing.ParsePrice(card.QuerySelector(".c-event-card__price-label")?.TextContent);

            yield return new Card(title, summary, href, venue, dateText, isFree, cost);
        }
    }

    private IEnumerable<EventOccurrence> BuildOccurrences(
        Card card, int? minAge, int? maxAge, DateOnly from, DateOnly to, DateTimeOffset now)
    {
        var (dates, start, timeKnown) = ParseSchedule(card.DateRangeText, from);
        var notes = card.Summary.Length > 0 ? card.Summary : null;

        foreach (var date in dates)
        {
            if (date < from || date > to) continue;

            yield return new EventOccurrence
            {
                ExternalKey = $"{SourceId}:{Slug(card.Url)}:{date:yyyy-MM-dd}",
                Source = SourceId,
                Category = Category,
                SourceUrl = card.Url,
                Date = date,
                StartTime = start,
                EndTime = null,
                TimeApproximate = !timeKnown,
                SessionName = card.Title,
                SessionNotes = notes,
                VenueName = card.Venue,
                VenueAddress = Address,
                Postcode = Postcode,
                MinAgeMonths = minAge,
                MaxAgeMonths = maxAge,
                TermTimeOnly = false,
                IsFree = card.IsFree,
                Cost = card.Cost,
                LastSeenAt = now,
            };
        }
    }

    // Hub-card date strings seen in the wild:
    //   "Fri 22 May – Fri 10 Jul 2026"   inclusive run  -> one row per day
    //   "Sat 23 May 2026, 11am"          single date + time
    //   "Sat 23 May 2026"                single date, no time
    //   "Wed 27 May & Thu 28 May 2026"   two discrete dates
    // Returns the concrete dates, the start time to stamp, and whether that
    // time came from the page (true) or is the placeholder (false).
    private static (List<DateOnly> Dates, TimeOnly Start, bool TimeKnown) ParseSchedule(string raw, DateOnly today)
    {
        var text = Regex.Replace(raw, @"\s+", " ").Trim();

        // Pull an optional trailing ", <time>" (e.g. ", 11am" / ", 10.30am").
        TimeOnly? time = null;
        var tm = Regex.Match(text, @",\s*(\d{1,2}(?:[.:]\d{2})?\s*(?:am|pm))\s*$", RegexOptions.IgnoreCase);
        if (tm.Success)
        {
            time = TextParsing.ParseClockTime(tm.Groups[1].Value);
            text = text[..tm.Index].Trim();
        }

        var dates = new List<DateOnly>();
        var rangeParts = Regex.Split(text, @"\s*[–—-]\s*");
        if (rangeParts.Length == 2
            && TryParseDate(rangeParts[0], rangeParts[1], today, out var rangeStart)
            && TryParseDate(rangeParts[1], rangeParts[1], today, out var rangeEnd))
        {
            // The start date carries no year of its own; if borrowing the end's
            // year put it after the end, the run spans a year boundary.
            if (rangeStart > rangeEnd) rangeStart = rangeStart.AddYears(-1);
            for (var d = rangeStart; d <= rangeEnd; d = d.AddDays(1)) dates.Add(d);
        }
        else
        {
            // One or more discrete dates joined by "&" / "and"; only the last
            // token carries the year.
            var tokens = Regex.Split(text, @"\s*(?:&|and)\s*", RegexOptions.IgnoreCase);
            var yearSource = tokens[^1];
            foreach (var tok in tokens)
                if (TryParseDate(tok, yearSource, today, out var d)) dates.Add(d);
        }

        return (dates, time ?? PlaceholderStart, time is not null);
    }

    // Parses "(Wday) 22 May (2026)". When the token omits the year it is taken
    // from yearSource (the year-bearing token of the same date string).
    private static bool TryParseDate(string token, string yearSource, DateOnly today, out DateOnly date)
    {
        date = default;
        var m = Regex.Match(token.Trim(), @"(\d{1,2})\s+([A-Za-z]+)(?:\s+(\d{4}))?", RegexOptions.IgnoreCase);
        if (!m.Success) return false;

        var day = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        if (!TryParseMonth(m.Groups[2].Value, out var month)) return false;

        int year;
        if (m.Groups[3].Success)
        {
            year = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        }
        else
        {
            var ym = Regex.Match(yearSource, @"\b(\d{4})\b");
            year = ym.Success ? int.Parse(ym.Groups[1].Value, CultureInfo.InvariantCulture) : today.Year;
        }

        try { date = new DateOnly(year, month, day); }
        catch (ArgumentOutOfRangeException) { return false; }
        return true;
    }

    private static bool TryParseMonth(string raw, out int month)
    {
        month = 0;
        if (DateTime.TryParseExact(raw, "MMMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            || DateTime.TryParseExact(raw, "MMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
        {
            month = dt.Month;
            return true;
        }
        return false;
    }

    private static string Slug(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
}
