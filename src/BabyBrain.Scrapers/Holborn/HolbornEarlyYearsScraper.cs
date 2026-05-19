using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using BabyBrain.Scrapers.Domain;
using BabyBrain.Scrapers.Shared;

namespace BabyBrain.Scrapers.Holborn;

// Source: https://www.holborncommunity.co.uk/activities/earlyyears-activities/
// The Holborn Community Association early-years page renders no events itself —
// it embeds a Plinth booking calendar in an iframe. We go straight to that
// iframe's URL (org 0u1BGc6H6BJXsoliFfeM, filtered to the "Early Years" tag).
//
// Plinth is a Next.js app: the events sit in the `__NEXT_DATA__` JSON blob as
// `props.pageProps.rawEvents`. Two carry the Early Years tag — a free weekly
// "Soft Play" drop-in and a paid "Preschool Gymnastics" course.
//
// Two recurrence shapes appear and we expand both:
//  * SPECIFIC   — an explicit `dates` list (minus `excludedDates`).
//  * BYWEEKDAY  — an iCal RRULE; we read BYDAY for the weekdays and treat
//                 every EXDATE as a cancelled occurrence.
// Plinth stores and *displays* the start/end timestamps as wall-clock time
// despite the trailing Z, so we read HH:MM literally with no timezone shift.
//
// `childEvents` (per-date overrides) are intentionally not processed — there
// is rarely more than one, and folding them in is far more complexity than
// the occasional rescheduled session is worth.
public sealed class HolbornEarlyYearsScraper : IScraper
{
    private const string PlinthEmbedUrl =
        "https://book.plinth.org.uk/embed/org/0u1BGc6H6BJXsoliFfeM?showFilters=false&showHeader=false&tag=Early%20Years";
    private const string PageUrl =
        "https://www.holborncommunity.co.uk/activities/earlyyears-activities/";
    private const string Tag = "Early Years";

    // Holborn Community Association's home venue — used when a Plinth event
    // carries no venue object of its own (e.g. Soft Play).
    private const string DefaultVenue = "Holborn House";
    private const string DefaultAddress = "35 Emerald Street, London";
    private const string DefaultPostcode = "WC1N 3QW";

    public string SourceId => "holborn_early_years";
    public string Category => Categories.Community;

    private readonly HttpClient _http;

    public HolbornEarlyYearsScraper(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<EventOccurrence>> ScrapeAsync(int horizonDays, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var horizonEnd = today.AddDays(horizonDays);
        var now = DateTimeOffset.UtcNow;

        var html = await _http.GetStringAsync(PlinthEmbedUrl, ct);
        var doc = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html), ct);

        var nextData = doc.QuerySelector("script#__NEXT_DATA__")?.TextContent;
        if (string.IsNullOrWhiteSpace(nextData))
            throw new InvalidOperationException("Holborn: Plinth __NEXT_DATA__ script not found");

        using var json = JsonDocument.Parse(nextData);
        if (!json.RootElement.TryGetProperty("props", out var props)
            || !props.TryGetProperty("pageProps", out var pageProps)
            || !pageProps.TryGetProperty("rawEvents", out var rawEvents)
            || rawEvents.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Holborn: rawEvents missing from Plinth data");

        var rows = new List<EventOccurrence>();
        foreach (var ev in rawEvents.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            if (HasTag(ev, Tag))
                rows.AddRange(BuildRows(ev, today, horizonEnd, now));
        }
        return rows;
    }

    private IEnumerable<EventOccurrence> BuildRows(JsonElement ev, DateOnly from, DateOnly to, DateTimeOffset now)
    {
        var name = ReadName(ev);
        var eventId = Str(ev, "_id");
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(eventId)) yield break;

        // A session with no readable start time is unusable.
        var start = ParseTimeOfDay(ev, "start");
        if (start is null) yield break;
        var end = ParseTimeOfDay(ev, "end");

        var (venueName, venueAddress, postcode) = ReadVenue(ev);
        var (isFree, cost) = ReadPrice(ev);
        var notes = ReadNotes(ev);
        var (minAge, maxAge) = ParseAge(name, notes ?? "");

        foreach (var date in OccurrenceDates(ev, from, to))
        {
            yield return new EventOccurrence
            {
                ExternalKey = $"{SourceId}:{eventId}:{date:yyyy-MM-dd}:{start:HHmm}",
                Source = SourceId,
                Category = Category,
                SourceUrl = PageUrl,
                Date = date,
                StartTime = start.Value,
                EndTime = end,
                SessionName = name,
                SessionNotes = notes,
                VenueName = venueName,
                VenueAddress = venueAddress,
                Postcode = postcode,
                MinAgeMonths = minAge,
                MaxAgeMonths = maxAge,
                // Recurrence data already excludes holidays (SPECIFIC lists
                // them; BYWEEKDAY EXDATEs cancel them), so every row is a
                // confirmed occurrence — no term-time caveat needed.
                TermTimeOnly = false,
                IsFree = isFree,
                Cost = cost,
                LastSeenAt = now,
            };
        }
    }

    // ---- recurrence -------------------------------------------------------

    private static IEnumerable<DateOnly> OccurrenceDates(JsonElement ev, DateOnly from, DateOnly to)
    {
        if (!ev.TryGetProperty("recurrence", out var rec) || rec.ValueKind != JsonValueKind.Object)
            return Array.Empty<DateOnly>();

        return (Str(rec, "type")) switch
        {
            "SPECIFIC" => SpecificDates(rec, from, to),
            "BYWEEKDAY" => ByWeekdayDates(rec, from, to),
            _ => Array.Empty<DateOnly>(),
        };
    }

    private static IEnumerable<DateOnly> SpecificDates(JsonElement rec, DateOnly from, DateOnly to)
    {
        var excluded = ReadIsoDateArray(rec, "excludedDates");
        foreach (var d in ReadIsoDateArray(rec, "dates"))
            if (d >= from && d <= to && !excluded.Contains(d))
                yield return d;
    }

    // BYWEEKDAY events carry an iCal RRULE — "FREQ=DAILY;BYDAY=MO,TU,TH,FR"
    // plus EXDATE lines. We materialise every in-window date whose weekday is
    // listed and which no EXDATE cancels.
    private static IEnumerable<DateOnly> ByWeekdayDates(JsonElement rec, DateOnly from, DateOnly to)
    {
        var rrule = Str(rec, "RRULE");
        if (string.IsNullOrEmpty(rrule)) return Array.Empty<DateOnly>();

        var byday = Regex.Match(rrule, @"BYDAY=([A-Z,]+)");
        if (!byday.Success) return Array.Empty<DateOnly>();
        var days = byday.Groups[1].Value.Split(',')
            .Select(ParseRRuleDay)
            .Where(d => d is not null)
            .Select(d => d!.Value)
            .ToHashSet();
        if (days.Count == 0) return Array.Empty<DateOnly>();

        // Every yyyyMMdd'T' token in the rule is an excluded (or DTSTART) date;
        // DTSTART landing in the set is harmless — it's years in the past.
        var excluded = Regex.Matches(rrule, @"(\d{4})(\d{2})(\d{2})T")
            .Select(m => SafeDate(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value))
            .Where(d => d is not null)
            .Select(d => d!.Value)
            .ToHashSet();

        var result = new List<DateOnly>();
        for (var d = from; d <= to; d = d.AddDays(1))
            if (days.Contains(d.DayOfWeek) && !excluded.Contains(d))
                result.Add(d);
        return result;
    }

    private static DayOfWeek? ParseRRuleDay(string token) => token.Trim().ToUpperInvariant() switch
    {
        "MO" => DayOfWeek.Monday,
        "TU" => DayOfWeek.Tuesday,
        "WE" => DayOfWeek.Wednesday,
        "TH" => DayOfWeek.Thursday,
        "FR" => DayOfWeek.Friday,
        "SA" => DayOfWeek.Saturday,
        "SU" => DayOfWeek.Sunday,
        _ => null,
    };

    private static HashSet<DateOnly> ReadIsoDateArray(JsonElement obj, string prop)
    {
        var set = new HashSet<DateOnly>();
        if (obj.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var e in arr.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String
                    && DateOnly.TryParse(e.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var d))
                    set.Add(d);
        return set;
    }

    private static DateOnly? SafeDate(string y, string m, string d)
    {
        try { return new DateOnly(int.Parse(y), int.Parse(m), int.Parse(d)); }
        catch { return null; }
    }

    // ---- field readers ----------------------------------------------------

    private static bool HasTag(JsonElement ev, string tag)
    {
        if (!ev.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var t in tags.EnumerateArray())
            if (t.ValueKind == JsonValueKind.String
                && string.Equals(t.GetString(), tag, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static string? ReadName(JsonElement ev) =>
        ev.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.Object
            ? Str(n, "text")?.Trim()
            : null;

    // ISO like "2026-05-02T08:30:00.000Z" — read HH:MM verbatim (see header).
    private static TimeOnly? ParseTimeOfDay(JsonElement ev, string prop)
    {
        var raw = Str(ev, prop);
        if (string.IsNullOrEmpty(raw)) return null;
        var m = Regex.Match(raw, @"T(\d{2}):(\d{2})");
        if (!m.Success) return null;
        var h = int.Parse(m.Groups[1].Value);
        var min = int.Parse(m.Groups[2].Value);
        return h <= 23 && min <= 59 ? new TimeOnly(h, min) : null;
    }

    private static (string name, string? address, string? postcode) ReadVenue(JsonElement ev)
    {
        if (ev.TryGetProperty("venue", out var v) && v.ValueKind == JsonValueKind.Object)
        {
            var name = Str(v, "name")?.Trim();
            if (!string.IsNullOrEmpty(name))
                return (name, NullIfEmpty(Str(v, "address")?.Trim()), NullIfEmpty(Str(v, "postcode")?.Trim()));
        }
        return (DefaultVenue, DefaultAddress, DefaultPostcode);
    }

    private static (bool isFree, decimal? cost) ReadPrice(JsonElement ev)
    {
        if (!ev.TryGetProperty("bookingDetails", out var bd) || bd.ValueKind != JsonValueKind.Object)
            return (false, null);
        if (bd.TryGetProperty("free", out var f) && f.ValueKind == JsonValueKind.True)
            return (true, null);
        // Plinth holds price in pence.
        if (bd.TryGetProperty("price", out var p) && p.ValueKind == JsonValueKind.Number
            && p.TryGetInt32(out var pence) && pence > 0)
            return (false, pence / 100m);
        return (false, null);
    }

    private static string? ReadNotes(JsonElement ev)
    {
        if (!ev.TryGetProperty("bookingDetails", out var bd)) return null;
        var raw = Str(bd, "description");
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = System.Net.WebUtility.HtmlDecode(Regex.Replace(raw, "<[^>]+>", " "));
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length == 0 ? null : Truncate(text, 400);
    }

    // Plinth event names carry the audience as e.g. "(3-5 Years)"; fall back to
    // the shared parser (which reads "Under 5s" etc. from the description).
    private static (int? min, int? max) ParseAge(string name, string description)
    {
        var m = Regex.Match(name, @"\((\d+)\s*[-–]\s*(\d+)\s*years?\)", RegexOptions.IgnoreCase);
        if (m.Success)
            return (int.Parse(m.Groups[1].Value) * 12, int.Parse(m.Groups[2].Value) * 12);
        return TextParsing.ParseAgeRange(name + " " + description);
    }

    private static string? Str(JsonElement obj, string prop) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out var v)
            && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";
}
