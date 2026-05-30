using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using BabyBrain.Scrapers.Domain;
using BabyBrain.Scrapers.Shared;

namespace BabyBrain.Scrapers.RoyalParks;

// Source: https://www.royalparks.org.uk/whats-on/play-in-the-park-26
// The Royal Parks' "Play in the Park" — free, nature-inspired, drop-in family
// play sessions (water play, allotment, wildlife, carnival) run across a handful
// of dates between spring and late summer in three parks. No booking.
//
// Server-rendered HTML behind a CDN that wants a browser UA, no JS challenge, so
// a plain HttpClient is enough — no Playwright. (If the CDN ever starts serving
// a Cloudflare interstitial, switch to PlaywrightFetcher like the rendered
// scrapers do.)
//
// The schedule lives in one `<h5>` park heading per park, each immediately
// followed by `<div class="responsive-table-wrapper"><table class="table">`.
// Every table's first <tr> is a header row of <strong> labels; the columns are
//   Date | Time | [Theme] | Location | Map Link
// where the Theme column is present only for Kensington Gardens. We read the
// header to map columns by name (so a missing Theme column doesn't shift the
// rest), then emit one dated row per data row that falls in the horizon.
//
// The dates carry no year ("11 August"), so we take the program year from the
// URL slug's trailing "-26" → 2026. The page is published one year at a time;
// when the 2027 programme lands at a new "-27" URL, bump the Url constant and
// the year falls out of the slug automatically.
public sealed class RoyalParksPlayInTheParkScraper : IScraper
{
    private const string Url = "https://www.royalparks.org.uk/whats-on/play-in-the-park-26";

    public string SourceId => "royal_parks_play_in_the_park";
    public string Category => Categories.Outdoors;

    private readonly HttpClient _http;

    public RoyalParksPlayInTheParkScraper(HttpClient http) => _http = http;

    // Per-park postcodes for geocoding/map pins — the table gives only a spot
    // within the park ("Buckhill Playground"), not an address. Keyed on the
    // park heading text. An unrecognised park still yields rows, just without a
    // postcode.
    private static readonly Dictionary<string, string> ParkPostcodes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Kensington Gardens"] = "W2 2UH",
            ["The Regent's Park"] = "NW1 4NR",
            ["Greenwich Park"] = "SE10 8QY",
        };

    public async Task<IReadOnlyList<EventOccurrence>> ScrapeAsync(int horizonDays, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var horizonEnd = today.AddDays(horizonDays);
        var now = DateTimeOffset.UtcNow;
        var year = ProgramYear(Url);
        var rows = new List<EventOccurrence>();

        var html = await _http.GetStringAsync(Url, ct);
        var doc = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html), ct);

        var tables = doc.QuerySelectorAll("table.table");
        if (tables.Length == 0)
            throw new InvalidOperationException("Royal Parks: no schedule tables found");

        foreach (var table in tables)
        {
            var park = NearestHeading(table);
            var trs = table.QuerySelectorAll("tr");
            if (trs.Length < 2) continue;

            // First row is the header — map column label -> index. A table that
            // doesn't carry the Date/Location columns we expect isn't a schedule
            // table; skip it rather than mis-parse.
            var header = ColumnIndex(trs[0]);
            if (!header.TryGetValue("date", out var iDate) || !header.TryGetValue("location", out var iLoc))
                continue;
            header.TryGetValue("time", out var iTime);
            var iTheme = header.TryGetValue("theme", out var t) ? t : -1;

            foreach (var tr in trs.Skip(1))
            {
                ct.ThrowIfCancellationRequested();
                var cells = tr.QuerySelectorAll("td");
                if (cells.Length <= iLoc) continue;

                var date = ParseDayMonth(Clean(cells[iDate].TextContent), year);
                if (date is null || date < today || date > horizonEnd) continue;

                var location = Clean(cells[iLoc].TextContent);
                var theme = iTheme >= 0 && cells.Length > iTheme ? Clean(cells[iTheme].TextContent) : "";
                var (start, end) = ParseTimeRange(iTime > 0 && cells.Length > iTime ? Clean(cells[iTime].TextContent) : null);

                var sessionName = theme.Length > 0 ? $"Play in the Park – {theme}" : "Play in the Park";
                var where = location.Length > 0 ? $"{location}, {park}" : park;
                var notes = $"Free, nature-inspired drop-in play at {where}. No need to book.";

                rows.Add(new EventOccurrence
                {
                    ExternalKey = $"{SourceId}:{Slug(park)}:{Slug(location)}:{date:yyyy-MM-dd}",
                    Source = SourceId,
                    Category = Category,
                    SourceUrl = Url,
                    Date = date.Value,
                    // Times are published ("12pm - 4pm"); placeholder noon only if
                    // a future edit drops the column, flagged approximate.
                    StartTime = start ?? new TimeOnly(12, 0),
                    EndTime = end,
                    TimeApproximate = start is null,
                    SessionName = sessionName,
                    SessionNotes = notes,
                    VenueName = park.Length > 0 ? park : "Royal Parks",
                    VenueAddress = where.Length > 0 ? where : null,
                    Postcode = ParkPostcodes.TryGetValue(park, out var pc) ? pc : null,
                    // Activities are pitched at "the whole family" with no age band
                    // published, so we leave the age range unknown.
                    MinAgeMonths = null,
                    MaxAgeMonths = null,
                    TermTimeOnly = false,
                    IsFree = true,
                    LastSeenAt = now,
                });
            }
        }

        return rows;
    }

    // Map each header cell's lower-cased label to its column index.
    private static Dictionary<string, int> ColumnIndex(IElement headerRow)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var cells = headerRow.QuerySelectorAll("td, th");
        for (var i = 0; i < cells.Length; i++)
        {
            var label = Clean(cells[i].TextContent).ToLowerInvariant();
            if (label.Length > 0 && !map.ContainsKey(label)) map[label] = i;
        }
        return map;
    }

    // The park name sits in the <h5> just before the table's wrapper div. Walk
    // back through previous siblings to the first heading, so a stray element
    // between heading and table doesn't lose us the park.
    private static string NearestHeading(IElement table)
    {
        var node = (table.ParentElement ?? table).PreviousElementSibling;
        while (node is not null)
        {
            if (node.NodeName is "H2" or "H3" or "H4" or "H5" or "H6")
            {
                var text = Clean(node.TextContent);
                // Skip the section heading ("Dates & Locations") — only a park
                // heading names a place.
                if (text.Length > 0 && !text.Contains("Dates", StringComparison.OrdinalIgnoreCase))
                    return text;
            }
            node = node.PreviousElementSibling;
        }
        return "";
    }

    // "31 March" / "11 August" -> a DateOnly in the given programme year.
    private static DateOnly? ParseDayMonth(string raw, int year)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        foreach (var fmt in new[] { "d MMMM", "dd MMMM", "d MMM", "dd MMM" })
        {
            if (DateTime.TryParseExact(raw, fmt, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var dt))
                return new DateOnly(year, dt.Month, dt.Day);
        }
        return null;
    }

    // "12pm - 4pm" / "10.30am – 12pm" / "2pm" (single, no end).
    private static (TimeOnly? Start, TimeOnly? End) ParseTimeRange(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, null);
        var parts = Regex.Split(raw.Trim(), @"\s*[–—-]\s*");
        var start = TextParsing.ParseClockTime(parts[0]);
        TimeOnly? end = parts.Length > 1 ? TextParsing.ParseClockTime(parts[1]) : null;
        return (start, end);
    }

    // Programme year from the URL slug's trailing "-NN" (e.g. "...-26" -> 2026).
    private static int ProgramYear(string url)
    {
        var m = Regex.Match(url, @"-(\d{2})(?:[/?#]|$)");
        return m.Success ? 2000 + int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : DateTime.UtcNow.Year;
    }

    // Collapse whitespace, including the &nbsp; the CMS sprinkles after labels.
    private static string Clean(string s) => Regex.Replace(s.Replace(' ', ' '), @"\s+", " ").Trim();

    private static string Slug(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
}
