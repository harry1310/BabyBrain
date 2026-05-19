using System.Text.Json;
using System.Text.Json.Serialization;
using AngleSharp;
using AngleSharp.Dom;
using BabyBrain.Scrapers.Domain;
using BabyBrain.Scrapers.Shared;
using Microsoft.Playwright;

namespace BabyBrain.Scrapers.Tockify;

// Tockify embeds schema.org JSON-LD <script type="application/ld+json"> arrays
// of Event objects in its rendered pages. We just deserialise.
// This scraper targets the Fitzrovia Community Centre children's calendar;
// other Tockify-powered orgs can be added by changing the URL.
public sealed class FitzroviaTockifyScraper : IScraper
{
    // Tockify renders ALL events into the page's schema.org JSON-LD regardless
    // of client-side category filters. Only ?search=… filters server-side, so
    // we fetch one URL per known baby/toddler session and merge. The full set
    // of children-section search terms (from fitzroviacommunitycentre.org/children-families)
    // also includes school-age clubs (Karma Kids 7-11, Whizz Kids/Challenge Lab/Sew
    // Good/Fitz Foodies — all "After School Club"); those are deliberately omitted.
    private static readonly SessionConfig[] Sessions =
    [
        new("stay & play",      MinAgeMonths: 0,  MaxAgeMonths: 60), // "Stay and Play group" general
        new("messy explorers",  MinAgeMonths: 6,  MaxAgeMonths: 60), // "Messy Fun For Little Ones"
        new("mini mozart",      MinAgeMonths: 0,  MaxAgeMonths: 60), // "Music sessions for under 5s"
        new("art adventures",   MinAgeMonths: 12, MaxAgeMonths: 60), // "for parents and toddlers"
    ];
    private const string BaseUrl = "https://calendar.fitzroviacommunitycentre.org/whatsonatfcc/pinboard";

    // Tockify emits startDate/endDate with the venue's offset (e.g. "+01:00" in BST).
    // DateTimeOffset.LocalDateTime would resolve to the *machine's* local time —
    // fine on a UK dev box but the production container runs UTC, so events came
    // out an hour early during BST. Always pin to Europe/London explicitly.
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    private record SessionConfig(string SearchTerm, int MinAgeMonths, int MaxAgeMonths);

    public string SourceId => "tockify_fitzrovia";
    public string Category => Categories.Community;

    private readonly PlaywrightFetcher _fetcher;
    public FitzroviaTockifyScraper(PlaywrightFetcher fetcher) => _fetcher = fetcher;

    public async Task<IReadOnlyList<EventOccurrence>> ScrapeAsync(int horizonDays, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var horizonEnd = DateOnly.FromDateTime(DateTime.UtcNow.Date).AddDays(horizonDays);
        var rows = new Dictionary<string, EventOccurrence>(); // dedupe by ExternalKey
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        foreach (var session in Sessions)
        {
            var url = $"{BaseUrl}?search={Uri.EscapeDataString(session.SearchTerm)}";
            var html = await _fetcher.FetchRenderedHtmlAsync(url, "script[type='application/ld+json']", WaitForSelectorState.Attached, ct);
            var doc = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html), ct);
            foreach (var ev in ExtractEvents(doc, opts, horizonEnd, now, session))
                rows[ev.ExternalKey] = ev;
        }
        return rows.Values.ToList();
    }

    private IEnumerable<EventOccurrence> ExtractEvents(IDocument doc, JsonSerializerOptions opts, DateOnly horizonEnd, DateTimeOffset now, SessionConfig session)
    {
        foreach (var script in doc.QuerySelectorAll("script[type='application/ld+json']"))
        {
            var raw = script.TextContent.Trim();
            if (string.IsNullOrWhiteSpace(raw)) continue;

            JsonDocument parsed;
            try { parsed = JsonDocument.Parse(raw); }
            catch (JsonException) { continue; }

            var items = parsed.RootElement.ValueKind == JsonValueKind.Array
                ? parsed.RootElement.EnumerateArray()
                : new[] { parsed.RootElement }.AsEnumerable();

            foreach (var item in items)
            {
                if (!item.TryGetProperty("@type", out var typeProp) || typeProp.GetString() != "Event")
                    continue;
                var ev = item.Deserialize<TockifyEvent>(opts);
                if (ev is null || ev.StartDate == default) continue;

                var localStart = TimeZoneInfo.ConvertTime(ev.StartDate, London).DateTime;
                var date = DateOnly.FromDateTime(localStart);
                if (date > horizonEnd) continue;

                yield return new EventOccurrence
                {
                    // Tockify URLs end in ".../detail/{eventId}/{occurrenceMs}" — stable across runs.
                    ExternalKey = $"{SourceId}:{ev.Url ?? $"{ev.Name}:{ev.StartDate.ToUnixTimeMilliseconds()}"}",
                    Source = SourceId,
                    Category = Category,
                    SourceUrl = ev.Url,
                    Date = date,
                    StartTime = TimeOnly.FromDateTime(localStart),
                    EndTime = ev.EndDate == default ? null : TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(ev.EndDate, London).DateTime),
                    SessionName = ev.Name ?? "Event",
                    SessionNotes = null,
                    VenueName = ev.Location?.Name ?? "Fitzrovia Community Centre",
                    VenueAddress = ev.Location?.Address is { } a
                        ? string.Join(", ", new[] { a.StreetAddress, a.Locality }.Where(s => !string.IsNullOrWhiteSpace(s)))
                        : null,
                    Postcode = ev.Location?.Address?.PostalCode,
                    MinAgeMonths = session.MinAgeMonths,
                    MaxAgeMonths = session.MaxAgeMonths,
                    TermTimeOnly = false,
                    IsFree = true,
                    LastSeenAt = now,
                };
            }
        }
    }

    private sealed class TockifyEvent
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("startDate")] public DateTimeOffset StartDate { get; set; }
        [JsonPropertyName("endDate")] public DateTimeOffset EndDate { get; set; }
        [JsonPropertyName("location")] public TockifyPlace? Location { get; set; }
    }

    private sealed class TockifyPlace
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("address")] public TockifyAddress? Address { get; set; }
    }

    private sealed class TockifyAddress
    {
        [JsonPropertyName("streetAddress")] public string? StreetAddress { get; set; }
        [JsonPropertyName("addressLocality")] public string? Locality { get; set; }
        [JsonPropertyName("postalCode")] public string? PostalCode { get; set; }
    }
}
