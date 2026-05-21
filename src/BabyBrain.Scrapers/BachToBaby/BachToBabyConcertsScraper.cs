using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using BabyBrain.Scrapers.Domain;
using BabyBrain.Scrapers.Shared;
using Microsoft.Playwright;

namespace BabyBrain.Scrapers.BachToBaby;

// Source: https://www.bachtobaby.com/whats-on-this-month
// Bach to Baby runs classical concerts for babies and young families. The
// "what's on this month" page is a server-rendered table covering the next
// ~3 months, grouped under <h2>Month Year</h2> headings; each concert is a
// <td class="date-block"> whose onclick names the venue's slug.
//
// The site sits behind a WAF that 403s .NET's HTTP client outright — it
// fingerprints the TLS handshake, so a plain HttpClient is blocked whatever
// User-Agent it sends. We therefore fetch through Playwright (a real browser),
// overriding the UA because the same WAF separately blocks a stock Chrome UA.
//
// Bach to Baby tours nationwide. Rather than follow every venue page (each
// also behind the WAF), London venues are curated below with their postcodes;
// a concert at any venue not in that list is dropped. Ticket price is uniform.
public sealed class BachToBabyConcertsScraper : IScraper
{
    private const string ListingUrl = "https://www.bachtobaby.com/whats-on-this-month";
    private const string Origin = "https://www.bachtobaby.com";

    // The WAF blocks a stock Chrome UA; this one is allowed.
    private const string UserAgent =
        "Mozilla/5.0 (compatible; BabyBrainScraper/1.0; +https://github.com/harry1310/BabyBrain)";

    // Bach to Baby's standard ticket — £18 on the door, uniform across venues.
    private const decimal TicketPrice = 18m;

    // Concerts run ~45 minutes.
    private const int DurationMinutes = 45;

    // "For baby and family" — newborn to school age.
    private const int MinAge = 0;
    private const int MaxAge = 60;

    // London Bach to Baby venues, keyed by the slug in each concert's onclick,
    // mapped to the venue postcode. Curated because every venue page is behind
    // the same WAF; a concert whose slug isn't here (a non-London venue, or a
    // new one) is skipped. Add new London venues as Bach to Baby opens them.
    private static readonly IReadOnlyDictionary<string, string> LondonVenues =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["west-hampstead"]        = "NW6 1JU",
            ["islington-angel"]       = "N1 2TX",
            ["wimbledon-central"]     = "SW19 4AA",
            ["walthamstow"]           = "E17 6PQ",
            ["twickenham-riverside"]  = "TW1 3NJ",
            ["south-kensington"]      = "SW7 4RL",
            ["regents-park-open-air"] = "NW1 4NU",
            ["pimlico"]               = "SW1V 2AD",
            ["kew"]                   = "TW9 4HF",
            ["highgate-wood"]         = "N6 4QH",
            ["greenwich"]             = "SE10 9EQ",
            ["east-dulwich"]          = "SE22 9AT",
            ["ealing"]                = "W5 2UP",
            ["bromley"]               = "BR1 1RY",
            ["victoria-park"]         = "E9 7EY",
            ["sydenham"]              = "SE26 6QR",
            ["surbiton"]              = "KT6 4LS",
            ["dulwich-village"]       = "SE21 7DG",
            ["blackheath"]            = "SE3 7SE",
        };

    public string SourceId => "bach_to_baby";
    public string Category => Categories.Concert;

    private readonly PlaywrightFetcher _fetcher;
    public BachToBabyConcertsScraper(PlaywrightFetcher fetcher) => _fetcher = fetcher;

    public async Task<IReadOnlyList<EventOccurrence>> ScrapeAsync(int horizonDays, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var horizonEnd = today.AddDays(horizonDays);
        var now = DateTimeOffset.UtcNow;

        var html = await _fetcher.FetchRenderedHtmlAsync(
            ListingUrl, "td.date-block", WaitForSelectorState.Attached, ct, userAgent: UserAgent);
        var doc = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html), ct);

        // Walk month headings and concert cells in document order so each
        // concert (which carries only a day-of-month) inherits its month.
        var rows = new List<EventOccurrence>();
        int? year = null, month = null;
        foreach (var el in doc.QuerySelectorAll("h2, td.date-block"))
        {
            ct.ThrowIfCancellationRequested();
            if (el.TagName.Equals("H2", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseMonthHeading(el.TextContent, out var y, out var m)) { year = y; month = m; }
                continue;
            }
            if (year is null || month is null) continue;
            if (!TryParseConcert(el, year.Value, month.Value, out var c)) continue;
            if (c.Date < today || c.Date > horizonEnd) continue;
            if (!LondonVenues.TryGetValue(c.Slug, out var postcode)) continue; // non-London / unknown

            rows.Add(new EventOccurrence
            {
                ExternalKey = $"{SourceId}:{c.Slug}:{c.Date:yyyy-MM-dd}:{c.Time:HHmm}",
                Source = SourceId,
                Category = Category,
                SourceUrl = $"{Origin}/{c.Slug}",
                Date = c.Date,
                StartTime = c.Time,
                EndTime = c.Time.AddMinutes(DurationMinutes),
                SessionName = $"Bach to Baby: {c.Title}",
                SessionNotes = c.Performers,
                VenueName = c.VenueName,
                VenueAddress = null,
                Postcode = postcode,
                MinAgeMonths = MinAge,
                MaxAgeMonths = MaxAge,
                TermTimeOnly = false,
                IsFree = false,
                Cost = TicketPrice,
                LastSeenAt = now,
            });
        }

        // ExternalKey is unique; collapse any accidental duplicate listing.
        return rows.GroupBy(r => r.ExternalKey).Select(g => g.First()).ToList();
    }

    private record RawConcert(string Slug, DateOnly Date, TimeOnly Time, string VenueName, string Title, string? Performers);

    // "May 2026" → (2026, 5). Other <h2>s on the page just don't match.
    private static bool TryParseMonthHeading(string text, out int year, out int month)
    {
        year = 0; month = 0;
        var m = Regex.Match(text.Trim(), @"^([A-Za-z]+)\s+(\d{4})$");
        if (!m.Success) return false;
        if (!DateTime.TryParseExact(m.Groups[1].Value, "MMMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return false;
        year = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        month = dt.Month;
        return true;
    }

    private static bool TryParseConcert(IElement cell, int year, int month, out RawConcert concert)
    {
        concert = null!;

        // onclick="location.href='/greenwich'" → the venue slug.
        var slugMatch = Regex.Match(cell.GetAttribute("onclick") ?? "", @"location\.href='/([^']+)'");
        if (!slugMatch.Success) return false;
        var slug = slugMatch.Groups[1].Value.Trim('/');

        if (!int.TryParse(cell.QuerySelector("p.date")?.TextContent?.Trim(), out var day)) return false;
        DateOnly date;
        try { date = new DateOnly(year, month, day); }
        catch (ArgumentOutOfRangeException) { return false; }

        var time = TextParsing.ParseClockTime(cell.QuerySelector("p.time")?.TextContent ?? "");
        if (time is null) return false;

        var venueName = cell.QuerySelector("p.location")?.TextContent?.Trim();
        if (string.IsNullOrEmpty(venueName)) return false;

        var title = cell.QuerySelector("p.text-title")?.TextContent?.Trim();
        if (string.IsNullOrEmpty(title)) return false;

        var performers = string.Join("; ", new[] { "p.text-line1", "p.text-line2" }
            .Select(s => cell.QuerySelector(s)?.TextContent?.Trim())
            .Where(s => !string.IsNullOrEmpty(s)));

        concert = new RawConcert(slug, date, time.Value, venueName, title,
            performers.Length == 0 ? null : performers);
        return true;
    }
}
