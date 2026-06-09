using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BabyBrain.Scrapers.Domain;

namespace BabyBrain.Scrapers.Tockify;

// Fitzrovia Community Centre runs its children's calendar on Tockify. We read
// events from Tockify's public "ngevent" JSON API directly.
//
// We used to render the pinboard SPA with Playwright and scrape the schema.org
// JSON-LD it embeds. That was fragile: Tockify attaches the JSON-LD <script>
// early and *populates* it as the page hydrates, so a render that grabbed the
// DOM mid-hydration captured only a partial event set — silently dropping
// sessions (this is what made "Art Adventures" go missing). The JSON API has no
// such race: it returns the full, structured result for a search server-side,
// needs no browser, and can't be half-loaded.
//
// API shape (GET https://tockify.com/api/ngevent):
//   calname=<calendar id from the URL path /whatsonatfcc/...>
//   startms,endms = window in epoch ms; search=<term> filters server-side
//   → { events: [ { eid:{uid,tid}, when:{start:{millis},end:{millis}},
//                   content:{ summary:{text}, place, address } } ], metaData }
public sealed class FitzroviaTockifyScraper : IScraper
{
    // Tockify filters server-side only on ?search=…, so we fetch one URL per
    // known baby/toddler session and merge. The full set of children-section
    // search terms (from fitzroviacommunitycentre.org/children-families) also
    // includes school-age clubs (Karma Kids 7-11, Whizz Kids/Challenge Lab/Sew
    // Good/Fitz Foodies — all "After School Club"); those are deliberately omitted.
    private static readonly SessionConfig[] Sessions =
    [
        new("stay & play",      MinAgeMonths: 0,  MaxAgeMonths: 60), // "Stay and Play group" general
        new("messy explorers",  MinAgeMonths: 6,  MaxAgeMonths: 60), // "Messy Fun For Little Ones"
        new("mini mozart",      MinAgeMonths: 0,  MaxAgeMonths: 60), // "Music sessions for under 5s"
        new("art adventures",   MinAgeMonths: 12, MaxAgeMonths: 60), // "for parents and toddlers"
    ];

    // Tockify calendar id (the first path segment of the FCC calendar URL,
    // /whatsonatfcc/pinboard) and the permalink base for an event's detail page.
    private const string Calname = "whatsonatfcc";
    private const string ApiBase = "https://tockify.com/api/ngevent";
    private const string DetailBase = "https://calendar.fitzroviacommunitycentre.org/whatsonatfcc/detail";

    // Some sessions on FCC's calendar are run by outside companies — FCC just
    // advertises them, with no price or booking link. For those we override the
    // details FCC omits from the operator's own site. Matched on a substring of
    // the event NAME, not the search term that returned it: Tockify events can
    // surface under several of our searches and the last-write-wins merge below
    // would otherwise tag an event by whichever search happened to land last.
    private static readonly Operator[] Operators =
    [
        // Mini Mozart under-5s music classes. £25 is their taster-class rate —
        // the cheapest way in; ongoing it's a monthly/annual subscription.
        new(NameMatch: "mini mozart",
            BookingUrl: "https://www.minimozart.com/product/fitzrovia/",
            FromCost: 25m),
    ];

    // Tockify gives start/end as absolute epoch ms (UTC). We pin to Europe/London
    // for the displayed date/time — DateTimeOffset.LocalDateTime would resolve to
    // the *machine's* local time (fine on a UK dev box, but the production
    // container runs UTC, so events came out an hour early during BST).
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    private static readonly Regex PostcodeRegex = new(
        @"\b([A-Z]{1,2}\d[A-Z\d]?\s*\d[A-Z]{2})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private record SessionConfig(string SearchTerm, int MinAgeMonths, int MaxAgeMonths);

    // A third-party operator that actually runs an event FCC merely advertises.
    // NameMatch is a case-insensitive substring of the event name. When an event
    // matches, it's treated as not-free, priced at the operator's "from" rate,
    // and its Source link points at the operator's booking page.
    private record Operator(string NameMatch, string BookingUrl, decimal FromCost);

    public string SourceId => "tockify_fitzrovia";
    public string Category => Categories.Community;

    private readonly HttpClient _http;
    public FitzroviaTockifyScraper(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<EventOccurrence>> ScrapeAsync(int horizonDays, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var horizonEnd = DateOnly.FromDateTime(DateTime.UtcNow.Date).AddDays(horizonDays);
        var startMs = now.ToUnixTimeMilliseconds();
        var endMs = now.AddDays(horizonDays + 1).ToUnixTimeMilliseconds();
        var rows = new Dictionary<string, EventOccurrence>(); // dedupe by ExternalKey

        foreach (var session in Sessions)
        {
            var url = $"{ApiBase}?calname={Calname}&max=500&startms={startMs}&endms={endMs}" +
                      $"&search={Uri.EscapeDataString(session.SearchTerm)}";
            var response = await _http.GetFromJsonAsync<TockifyResponse>(url, ct);
            foreach (var ev in ExtractEvents(response, horizonEnd, now, session))
                rows[ev.ExternalKey] = ev;
        }
        return rows.Values.ToList();
    }

    private IEnumerable<EventOccurrence> ExtractEvents(
        TockifyResponse? response, DateOnly horizonEnd, DateTimeOffset now, SessionConfig session)
    {
        foreach (var ev in response?.Events ?? [])
        {
            var startMs = ev.When?.Start?.Millis;
            if (startMs is null) continue;

            var startUtc = DateTimeOffset.FromUnixTimeMilliseconds(startMs.Value);
            var localStart = TimeZoneInfo.ConvertTime(startUtc, London).DateTime;
            var date = DateOnly.FromDateTime(localStart);
            if (date > horizonEnd) continue;

            var name = ev.Content?.Summary?.Text ?? "Event";
            var op = Operators.FirstOrDefault(
                o => name.Contains(o.NameMatch, StringComparison.OrdinalIgnoreCase));

            // Tockify's own detail permalink: /detail/{eventId}/{occurrenceMs}.
            // Built to byte-match the URL the old JSON-LD path emitted, so the
            // ExternalKey identity is unchanged across this migration.
            var uid = ev.Eid?.Uid;
            var detailUrl = uid is null ? null : $"{DetailBase}/{uid}/{startMs.Value}";

            var endMs = ev.When?.End?.Millis;
            var (address, postcode) = ParseAddress(ev.Content?.Address);

            yield return new EventOccurrence
            {
                ExternalKey = $"{SourceId}:{detailUrl ?? $"{name}:{startMs.Value}"}",
                Source = SourceId,
                Category = Category,
                // FCC's own sessions link to their detail page; an operator-run
                // event links to the operator's booking page instead.
                SourceUrl = op?.BookingUrl ?? detailUrl,
                Date = date,
                StartTime = TimeOnly.FromDateTime(localStart),
                EndTime = endMs is null
                    ? null
                    : TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(
                        DateTimeOffset.FromUnixTimeMilliseconds(endMs.Value), London).DateTime),
                SessionName = name,
                SessionNotes = null,
                VenueName = string.IsNullOrWhiteSpace(ev.Content?.Place)
                    ? "Fitzrovia Community Centre"
                    : ev.Content!.Place,
                VenueAddress = address,
                Postcode = postcode,
                MinAgeMonths = session.MinAgeMonths,
                MaxAgeMonths = session.MaxAgeMonths,
                TermTimeOnly = false,
                // FCC's own sessions are free; an operator-run event is not —
                // show the operator's "from" price instead.
                IsFree = op is null,
                Cost = op?.FromCost,
                LastSeenAt = now,
            };
        }
    }

    // Tockify gives the location as one flat string, e.g.
    // "2 Foley St, London W1W 6DL, UK". Split out the postcode and drop the
    // trailing country so VenueAddress reads like the rest of the corpus.
    private static (string? address, string? postcode) ParseAddress(string? full)
    {
        if (string.IsNullOrWhiteSpace(full)) return (null, null);

        var pc = PostcodeRegex.Match(full);
        var postcode = pc.Success ? pc.Value.ToUpperInvariant() : null;

        var addr = pc.Success ? full.Remove(pc.Index, pc.Length) : full;
        addr = Regex.Replace(addr, @",?\s*(UK|United Kingdom)\s*$", "", RegexOptions.IgnoreCase);
        addr = Regex.Replace(addr, @"\s+", " ");
        addr = Regex.Replace(addr, @"\s*,(\s*,)+", ",");      // collapse empty ", ," runs
        addr = addr.Trim().Trim(',').Trim();

        return (string.IsNullOrWhiteSpace(addr) ? null : addr, postcode);
    }

    private sealed class TockifyResponse
    {
        [JsonPropertyName("events")] public List<TockifyEvent>? Events { get; set; }
    }

    private sealed class TockifyEvent
    {
        [JsonPropertyName("eid")] public TockifyEid? Eid { get; set; }
        [JsonPropertyName("when")] public TockifyWhen? When { get; set; }
        [JsonPropertyName("content")] public TockifyContent? Content { get; set; }
    }

    private sealed class TockifyEid
    {
        [JsonPropertyName("uid")] public string? Uid { get; set; }
        [JsonPropertyName("tid")] public long Tid { get; set; }
    }

    private sealed class TockifyWhen
    {
        [JsonPropertyName("start")] public TockifyInstant? Start { get; set; }
        [JsonPropertyName("end")] public TockifyInstant? End { get; set; }
    }

    private sealed class TockifyInstant
    {
        [JsonPropertyName("millis")] public long? Millis { get; set; }
    }

    private sealed class TockifyContent
    {
        [JsonPropertyName("summary")] public TockifyText? Summary { get; set; }
        [JsonPropertyName("place")] public string? Place { get; set; }
        [JsonPropertyName("address")] public string? Address { get; set; }
    }

    private sealed class TockifyText
    {
        [JsonPropertyName("text")] public string? Text { get; set; }
    }
}
