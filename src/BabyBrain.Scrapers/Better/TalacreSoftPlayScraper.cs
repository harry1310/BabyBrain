using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using BabyBrain.Scrapers.Domain;

namespace BabyBrain.Scrapers.Better;

// Source: https://www.better.org.uk/leisure-centre/london/camden/talacre-community-sports-centre/soft-play
//
// That public page is marketing only — the bookable sessions live in Better's
// (GLL) booking API:
//   GET better-admin.org.uk/api/activities/venue/{venue}/activity/{activity}/times?date=YYYY-MM-DD
// which needs an Origin header matching the booking sub-domain (the HttpClient
// is configured with it in Program.cs).
//
// Soft play is a rolling drop-in facility: the API lists ~35 staggered 1-hour
// slots per day (every 15 min, ~09:00 onwards), all named "Soft Play". Emitting
// every slot would bury the rest of the directory, so we collapse each day into
// ONE row spanning the first slot's start to the last slot's end, linking to
// that day's booking page. The booking API doesn't expose a price (that lives
// behind the authenticated checkout flow), so Cost is left unknown.
public sealed class TalacreSoftPlayScraper : IScraper
{
    private const string VenueSlug = "talacre-community-sports-centre";
    private const string ActivitySlug = "soft-play";
    private const string ApiBase = "https://better-admin.org.uk/api/activities/venue";
    private const string BookingBase = "https://bookings.better.org.uk/location/talacre-community-sports-centre/soft-play";

    private const string Venue = "Talacre Community Sports Centre";
    private const string Address = "Dalby Street, Kentish Town, London";
    private const string Postcode = "NW5 3AF";

    // Soft play is for children up to 10; the venue has a dedicated under-3s area.
    private const int MinAgeMonths = 0;
    private const int MaxAgeMonths = 120;

    // Stop probing once this many consecutive days return no slots — we've run
    // past the venue's bookable window rather than hammering the API to the
    // full horizon.
    private const int EmptyDayRunLimit = 14;

    private const string Notes =
        "Drop-in soft play, bookable in 1-hour slots through the day. " +
        "For children up to 10, with a dedicated under-3s area. Socks must be worn.";

    public string SourceId => "better_talacre_soft_play";
    public string Category => Categories.Community;

    private readonly HttpClient _http;

    public TalacreSoftPlayScraper(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<EventOccurrence>> ScrapeAsync(int horizonDays, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var now = DateTimeOffset.UtcNow;
        var rows = new List<EventOccurrence>();
        var emptyRun = 0;

        for (var offset = 0; offset <= horizonDays; offset++)
        {
            ct.ThrowIfCancellationRequested();
            var date = today.AddDays(offset);

            var window = await TryFetchDayWindowAsync(date, ct);
            if (window is null)
            {
                if (++emptyRun >= EmptyDayRunLimit) break;
                continue;
            }
            emptyRun = 0;

            var (start, end) = window.Value;
            rows.Add(new EventOccurrence
            {
                ExternalKey = $"{SourceId}:{date:yyyy-MM-dd}",
                Source = SourceId,
                Category = Category,
                SourceUrl = $"{BookingBase}/{date:yyyy-MM-dd}/by-time",
                Date = date,
                StartTime = start,
                EndTime = end,
                SessionName = "Soft Play",
                SessionNotes = Notes,
                VenueName = Venue,
                VenueAddress = Address,
                Postcode = Postcode,
                MinAgeMonths = MinAgeMonths,
                MaxAgeMonths = MaxAgeMonths,
                TermTimeOnly = false,
                IsFree = false,
                LastSeenAt = now,
            });
        }
        return rows;
    }

    // Returns the day's overall soft-play window (earliest slot start, latest
    // slot end), or null if the day has no slots or the request failed.
    private async Task<(TimeOnly start, TimeOnly end)?> TryFetchDayWindowAsync(DateOnly date, CancellationToken ct)
    {
        try
        {
            var url = $"{ApiBase}/{VenueSlug}/activity/{ActivitySlug}/times?date={date:yyyy-MM-dd}";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var payload = await resp.Content.ReadFromJsonAsync<TimesResponse>(ct);
            var slots = payload?.Data;
            if (slots is null || slots.Count == 0) return null;

            TimeOnly? earliest = null, latest = null;
            foreach (var slot in slots)
            {
                if (TryParseTime(slot.StartsAt?.Format24Hour) is { } s && (earliest is null || s < earliest))
                    earliest = s;
                if (TryParseTime(slot.EndsAt?.Format24Hour) is { } e && (latest is null || e > latest))
                    latest = e;
            }
            if (earliest is null || latest is null) return null;
            return (earliest.Value, latest.Value);
        }
        catch
        {
            return null;
        }
    }

    private static TimeOnly? TryParseTime(string? hhmm)
    {
        if (string.IsNullOrEmpty(hhmm)) return null;
        return TimeOnly.TryParseExact(hhmm, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t : null;
    }

    private sealed record TimesResponse(
        [property: JsonPropertyName("data")] List<Slot>? Data);

    private sealed record Slot(
        [property: JsonPropertyName("starts_at")] ClockTime? StartsAt,
        [property: JsonPropertyName("ends_at")] ClockTime? EndsAt);

    private sealed record ClockTime(
        [property: JsonPropertyName("format_24_hour")] string? Format24Hour);
}
