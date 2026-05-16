using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using BabyBrain.Scrapers.Domain;
using BabyBrain.Scrapers.Shared;

namespace BabyBrain.Scrapers.DesignMuseum;

// Source: https://designmuseum.org/whats-on/families-and-young-people
// Server-rendered HTML, no schema.org microdata. Each event sits in a
// <div class="page-item"> with a <time class="icon-date"> carrying the
// date and time range as free text, e.g. "Friday 29 May, 10:15 – 16:15".
// Evergreen entries on the same page (Plan Your Visit, online activities,
// the permanent collection) either omit the <time> entirely or use it for
// a non-date label like "Free Permanent Display" — both are filtered out
// because we can't parse a date and time range from them.
//
// A handful of cards list two alternative dates joined by " or ", e.g.
// "Thursday 28 May or Tuesday 28 July, 10:15 – 16:15" — we emit one row
// per date. The listing omits years entirely; we resolve each date to the
// soonest future year whose calendar matches the given day-of-week.
public sealed class DesignMuseumFamiliesScraper : IScraper
{
    private const string ListingUrl = "https://designmuseum.org/whats-on/families-and-young-people";
    private const string Origin = "https://designmuseum.org";
    private const string Venue = "Design Museum";
    private const string Address = "224-238 Kensington High Street, London";
    private const string Postcode = "W8 6AG";

    public string SourceId => "design_museum_families";
    public string Category => Categories.Museum;

    private readonly HttpClient _http;

    public DesignMuseumFamiliesScraper(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<EventOccurrence>> ScrapeAsync(int horizonDays, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var horizonEnd = today.AddDays(horizonDays);
        var now = DateTimeOffset.UtcNow;
        var rows = new List<EventOccurrence>();

        var html = await _http.GetStringAsync(ListingUrl, ct);
        var doc = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html), ct);

        foreach (var card in doc.QuerySelectorAll("div.page-item"))
        {
            ct.ThrowIfCancellationRequested();
            rows.AddRange(BuildRows(card, today, horizonEnd, now));
        }

        // The listing surfaces some events under more than one section heading
        // (e.g. a single Shoe Prototype workshop appears in both the May and
        // July groups). ExternalKey has a unique constraint, so collapse to
        // one row per occurrence.
        return rows
            .GroupBy(r => r.ExternalKey)
            .Select(g => g.First())
            .ToList();
    }

    private IEnumerable<EventOccurrence> BuildRows(IElement card, DateOnly from, DateOnly to, DateTimeOffset now)
    {
        var dateText = card.QuerySelector("time.icon-date")?.TextContent?.Trim();
        if (string.IsNullOrEmpty(dateText)) yield break;

        var (startTime, endTime) = ParseTimeRange(dateText);
        if (startTime is null) yield break;

        var title = card.QuerySelector("h2")?.TextContent?.Trim();
        if (string.IsNullOrEmpty(title)) yield break;

        var href = card.QuerySelector("a[href]")?.GetAttribute("href");
        if (string.IsNullOrEmpty(href)) yield break;
        var url = href.StartsWith("http") ? href : Origin + href;
        var eventId = ExtractEventId(href);

        var description = card.QuerySelector("div.rich-text")?.TextContent?.Trim();
        var notes = string.IsNullOrEmpty(description) ? null : Truncate(description, 400);

        var (minAge, maxAge) = TextParsing.ParseAgeRange(title + " " + (description ?? ""));

        foreach (var date in ParseDates(dateText, from))
        {
            if (date < from || date > to) continue;
            yield return new EventOccurrence
            {
                ExternalKey = $"{SourceId}:{eventId}:{date:yyyy-MM-dd}:{startTime:HHmm}",
                Source = SourceId,
                Category = Category,
                SourceUrl = url,
                Date = date,
                StartTime = startTime.Value,
                EndTime = endTime,
                SessionName = title,
                SessionNotes = notes,
                VenueName = Venue,
                VenueAddress = Address,
                Postcode = Postcode,
                MinAgeMonths = minAge,
                MaxAgeMonths = maxAge,
                TermTimeOnly = false,
                IsFree = false,
                LastSeenAt = now,
            };
        }
    }

    // "Friday 29 May, 10:15 – 16:15" or
    // "Thursday 28 May or Tuesday 28 July, 10:15 – 16:15"
    // → strip the trailing time tail (after the last comma) and split the
    // leading segment on " or " — each piece is "<DOW> <day> <Month>".
    private static IEnumerable<DateOnly> ParseDates(string text, DateOnly today)
    {
        var commaIdx = text.LastIndexOf(',');
        var dateSegment = commaIdx >= 0 ? text[..commaIdx] : text;

        foreach (var piece in Regex.Split(dateSegment, @"\s+or\s+", RegexOptions.IgnoreCase))
        {
            var d = ParseSingleDate(piece.Trim(), today);
            if (d is not null) yield return d.Value;
        }
    }

    private static DateOnly? ParseSingleDate(string piece, DateOnly today)
    {
        var m = Regex.Match(piece, @"^(?<dow>[A-Za-z]+)?\s*(?<day>\d{1,2})\s+(?<mon>[A-Za-z]+)$");
        if (!m.Success) return null;
        if (!int.TryParse(m.Groups["day"].Value, out var day)) return null;
        var month = ParseMonth(m.Groups["mon"].Value);
        if (month is null) return null;

        var dow = m.Groups["dow"].Success
            ? TextParsing.ParseDayOfWeek(m.Groups["dow"].Value)
            : null;
        // Year is implicit — walk forward up to 2 years to find a candidate
        // that's not in the past and (if a DOW was given) matches the
        // weekday written on the card.
        for (var year = today.Year; year <= today.Year + 2; year++)
        {
            DateOnly candidate;
            try { candidate = new DateOnly(year, month.Value, day); }
            catch { continue; }
            if (candidate < today) continue;
            if (dow is not null && candidate.DayOfWeek != dow.Value) continue;
            return candidate;
        }
        return null;
    }

    private static int? ParseMonth(string s) => s.Trim().ToLowerInvariant() switch
    {
        "january" or "jan" => 1,
        "february" or "feb" => 2,
        "march" or "mar" => 3,
        "april" or "apr" => 4,
        "may" => 5,
        "june" or "jun" => 6,
        "july" or "jul" => 7,
        "august" or "aug" => 8,
        "september" or "sep" or "sept" => 9,
        "october" or "oct" => 10,
        "november" or "nov" => 11,
        "december" or "dec" => 12,
        _ => null,
    };

    private static (TimeOnly? start, TimeOnly? end) ParseTimeRange(string text)
    {
        var m = Regex.Match(text, @"(\d{1,2}:\d{2})\s*[–\-]\s*(\d{1,2}:\d{2})");
        if (!m.Success) return (null, null);
        return (TextParsing.ParseClockTime(m.Groups[1].Value),
                TextParsing.ParseClockTime(m.Groups[2].Value));
    }

    private static string ExtractEventId(string href)
    {
        // Hrefs are shaped /whats-on/<section>/<slug>. The trailing slug is
        // stable across re-scrapes, so use it as the per-event id.
        var trimmed = href.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        return lastSlash >= 0 ? trimmed[(lastSlash + 1)..] : trimmed;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";
}
