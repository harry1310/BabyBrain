using System.Globalization;
using System.Net;
using System.Text.Json;
using AngleSharp;
using AngleSharp.Dom;
using BabyBrain.Scrapers.Domain;
using BabyBrain.Scrapers.Shared;

namespace BabyBrain.Scrapers.BritishLibrary;

// Source: https://events.bl.uk/whats-on/london-family-events
// The British Library's curated "London Family Events" listing. Server-rendered
// HTML (no Cloudflare, no JS challenge), so a plain browser-UA HttpClient is
// enough — no Playwright.
//
// The listing links to ~a dozen /events/<slug> detail pages. Unusually for our
// sources, each detail page embeds clean schema.org Event JSON-LD — ONE block
// per session ("sub-event"), each with an ISO startDate, full description and
// the venue address. So we walk listing -> detail and emit one row per Event
// block, reading the structured JSON rather than scraping the DOM. A single
// event with two sittings (e.g. an under-5s workshop at 09.45 and 11.00) yields
// two blocks with distinct startDates; a year-long drop-in yields one block per
// real session date — no recurrence expansion needed on our side.
//
// JSON-LD gives no endDate (so EndTime is left null) and no price; the listing
// card carries the price label ("Free"), which we read per card. Age isn't
// structured, so we infer it from the title/description text.
public sealed class BritishLibraryFamilyScraper : IScraper
{
    private const string ListingUrl = "https://events.bl.uk/whats-on/london-family-events";
    private const string Origin = "https://events.bl.uk";

    public string SourceId => "british_library_family";
    public string Category => Categories.Library;

    private readonly HttpClient _http;

    public BritishLibraryFamilyScraper(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<EventOccurrence>> ScrapeAsync(int horizonDays, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var horizonEnd = today.AddDays(horizonDays);
        var now = DateTimeOffset.UtcNow;
        var rows = new List<EventOccurrence>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var listingHtml = await _http.GetStringAsync(ListingUrl, ct);
        var listing = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(listingHtml), ct);

        var cards = ExtractCards(listing).ToList();
        if (cards.Count == 0)
            throw new InvalidOperationException("British Library: no event cards found on listing page");

        foreach (var card in cards)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var detailHtml = await _http.GetStringAsync(card.Url, ct);
                foreach (var ev in ParseEvents(detailHtml))
                {
                    if (ev.Date < today || ev.Date > horizonEnd) continue;

                    var key = $"{SourceId}:{Slug(card.Url)}:{ev.Date:yyyy-MM-dd}:{ev.Start:HHmm}";
                    if (!seen.Add(key)) continue;

                    // Age from the title only ("Under 5s Workshop" -> 0-60mo).
                    // The descriptions mention "baby"/"toddler" incidentally
                    // (e.g. a festival's baby area), which the shared keyword
                    // fallback would wrongly read as a baby-only age band — a
                    // wrong age is worse than none in an age-filtered directory.
                    var (minAge, maxAge) = TextParsing.ParseAgeRange(ev.Name);
                    var (isFree, cost) = TextParsing.ParsePrice(card.PriceLabel);

                    rows.Add(new EventOccurrence
                    {
                        ExternalKey = key,
                        Source = SourceId,
                        Category = Category,
                        SourceUrl = card.Url,
                        Date = ev.Date,
                        StartTime = ev.Start,
                        EndTime = null, // schema.org Event gives startDate only
                        TimeApproximate = false,
                        SessionName = ev.Name,
                        SessionNotes = ev.Description.Length > 0 ? Truncate(ev.Description, 400) : null,
                        VenueName = ev.VenueName.Length > 0 ? ev.VenueName : "British Library",
                        VenueAddress = ev.VenueAddress.Length > 0 ? ev.VenueAddress : "96 Euston Road, London",
                        Postcode = ev.Postcode.Length > 0 ? ev.Postcode : "NW1 2DB",
                        MinAgeMonths = minAge,
                        MaxAgeMonths = maxAge,
                        TermTimeOnly = false,
                        IsFree = isFree,
                        Cost = cost,
                        LastSeenAt = now,
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  skipped {card.Url}: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
            }
        }

        AddFairyTalesExhibition(rows, today, horizonEnd, now, seen);

        return rows;
    }

    // Special-case sub-source: the "Fairy Tales" exhibition
    // (events.bl.uk/exhibitions/fairy-tales). Unlike the /events/ sessions this
    // scraper reads from JSON-LD, an exhibition is a date-RANGE drop-in: it isn't
    // on the family-events listing, lives under /exhibitions/, and carries no
    // schema.org Event blocks — so the normal path can't see it. It's hand-modelled
    // here as one drop-in row per day across its run, clamped to the scrape horizon.
    // Hardcoded on purpose (fixed-run exhibition, stable facts); it stops emitting
    // rows by itself once past FairyTalesEnd. Remove this once it has closed.
    private const string FairyTalesUrl = "https://events.bl.uk/exhibitions/fairy-tales";
    private static readonly DateOnly FairyTalesStart = new(2026, 3, 27);
    private static readonly DateOnly FairyTalesEnd = new(2026, 8, 23);

    private void AddFairyTalesExhibition(
        List<EventOccurrence> rows, DateOnly today, DateOnly horizonEnd, DateTimeOffset now, HashSet<string> seen)
    {
        var from = today > FairyTalesStart ? today : FairyTalesStart;
        var to = horizonEnd < FairyTalesEnd ? horizonEnd : FairyTalesEnd;

        for (var d = from; d <= to; d = d.AddDays(1))
        {
            // 10:00 is a placeholder opening time, hence TimeApproximate — an
            // exhibition is a drop-in, not a timed session.
            var key = $"{SourceId}:fairy-tales:{d:yyyy-MM-dd}:1000";
            if (!seen.Add(key)) continue;

            rows.Add(new EventOccurrence
            {
                ExternalKey = key,
                Source = SourceId,
                Category = Category,
                SourceUrl = FairyTalesUrl,
                Date = d,
                StartTime = new TimeOnly(10, 0),
                EndTime = null,
                TimeApproximate = true,
                SessionName = "Fairy Tales (exhibition)",
                SessionNotes = "Drop-in family exhibition — visit any time during Library opening hours. "
                    + "An interactive journey through enchanted lands, magical creatures and classic tales. "
                    + "Aimed at ages 3–10; under 1s go free. Tickets £11.50 off-peak / £13.50 peak, concessions available.",
                VenueName = "British Library",
                VenueAddress = "96 Euston Road, London",
                Postcode = "NW1 2DB",
                MinAgeMonths = 36,
                MaxAgeMonths = 120,
                TermTimeOnly = false,
                IsFree = false,
                Cost = 11.50m,
                LastSeenAt = now,
            });
        }
    }

    private record Card(string Url, string PriceLabel);

    private record ParsedEvent(
        string Name, DateOnly Date, TimeOnly Start, string Description,
        string VenueName, string VenueAddress, string Postcode);

    private static IEnumerable<Card> ExtractCards(IDocument listing)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in listing.QuerySelectorAll("a.c-media--event"))
        {
            var href = a.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href) || !href.Contains("/events/", StringComparison.OrdinalIgnoreCase))
                continue;
            var abs = href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? href : Origin + href;
            if (!seen.Add(abs)) continue;
            var priceLabel = a.QuerySelector(".c-media__labels")?.TextContent.Trim() ?? "";
            yield return new Card(abs, priceLabel);
        }
    }

    // Each detail page carries one or more <script type="application/ld+json">
    // blocks; we want the @type == "Event" ones (one per session).
    private static IEnumerable<ParsedEvent> ParseEvents(string html)
    {
        // Pull the script bodies without a full DOM parse — they're plain JSON.
        foreach (var json in JsonLdBlocks(html))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); }
            catch (JsonException) { continue; }

            using (doc)
            {
                foreach (var obj in EnumerateObjects(doc.RootElement))
                {
                    if (!IsType(obj, "Event")) continue;
                    if (!obj.TryGetProperty("startDate", out var sd) || sd.ValueKind != JsonValueKind.String) continue;
                    if (!DateTimeOffset.TryParse(sd.GetString(), CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind, out var start)) continue;

                    // JSON-LD lives inside the HTML, so its string values are
                    // entity-encoded ("Coelho&#039;s"); some are double-encoded
                    // ("&amp;#039;"), so decode twice.
                    var name = Decode(GetString(obj, "name"));
                    if (name.Length == 0) continue;
                    var description = Decode(GetString(obj, "description"));
                    var (venueName, venueAddress, postcode) = ParseLocation(obj);

                    // startDate carries a London offset (e.g. +01:00); the local
                    // wall-clock component is the time families see, so keep it.
                    yield return new ParsedEvent(
                        name,
                        DateOnly.FromDateTime(start.DateTime),
                        TimeOnly.FromDateTime(start.DateTime),
                        description, venueName, venueAddress, postcode);
                }
            }
        }
    }

    private static (string Name, string Address, string Postcode) ParseLocation(JsonElement ev)
    {
        if (!ev.TryGetProperty("location", out var loc) || loc.ValueKind != JsonValueKind.Object)
            return ("", "", "");
        var name = GetString(loc, "name");
        var street = ""; var locality = ""; var postcode = "";
        if (loc.TryGetProperty("address", out var addr) && addr.ValueKind == JsonValueKind.Object)
        {
            street = GetString(addr, "streetAddress");
            locality = GetString(addr, "addressLocality");
            postcode = GetString(addr, "postalCode");
        }
        var address = string.Join(", ", new[] { street, locality }.Where(s => s.Length > 0));
        return (name, address, postcode);
    }

    // Yield each JSON object that might be a node: the root, array elements, and
    // any @graph members.
    private static IEnumerable<JsonElement> EnumerateObjects(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in root.EnumerateArray())
                foreach (var inner in EnumerateObjects(el))
                    yield return inner;
            yield break;
        }
        if (root.ValueKind != JsonValueKind.Object) yield break;
        yield return root;
        if (root.TryGetProperty("@graph", out var graph) && graph.ValueKind == JsonValueKind.Array)
            foreach (var el in graph.EnumerateArray())
                foreach (var inner in EnumerateObjects(el))
                    yield return inner;
    }

    // @type can be a string or an array of strings.
    private static bool IsType(JsonElement obj, string type)
    {
        if (!obj.TryGetProperty("@type", out var t)) return false;
        if (t.ValueKind == JsonValueKind.String)
            return string.Equals(t.GetString(), type, StringComparison.OrdinalIgnoreCase);
        if (t.ValueKind == JsonValueKind.Array)
            foreach (var el in t.EnumerateArray())
                if (el.ValueKind == JsonValueKind.String &&
                    string.Equals(el.GetString(), type, StringComparison.OrdinalIgnoreCase))
                    return true;
        return false;
    }

    private static string GetString(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!.Trim() : "";

    // Extract the bodies of <script type="application/ld+json"> ... </script>.
    private static IEnumerable<string> JsonLdBlocks(string html)
    {
        const string open = "application/ld+json";
        var i = 0;
        while (true)
        {
            var marker = html.IndexOf(open, i, StringComparison.OrdinalIgnoreCase);
            if (marker < 0) yield break;
            var gt = html.IndexOf('>', marker);
            if (gt < 0) yield break;
            var end = html.IndexOf("</script>", gt, StringComparison.OrdinalIgnoreCase);
            if (end < 0) yield break;
            yield return html[(gt + 1)..end].Trim();
            i = end + 9;
        }
    }

    // HTML-decode twice: values are entity-encoded once, a few descriptions twice.
    private static string Decode(string s) => WebUtility.HtmlDecode(WebUtility.HtmlDecode(s)).Trim();

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";

    private static string Slug(string url)
    {
        var path = url.TrimEnd('/');
        var slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }
}
