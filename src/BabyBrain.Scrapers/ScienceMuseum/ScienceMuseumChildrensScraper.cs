using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using BabyBrain.Scrapers.Domain;
using BabyBrain.Scrapers.Shared;
using Microsoft.Extensions.Logging;

namespace BabyBrain.Scrapers.ScienceMuseum;

// Source: https://www.sciencemuseum.org.uk/see-and-do
//
// The Science Museum's "See and do" index lists everything — galleries,
// exhibitions, films, tours, ticketed shows — with no age filter. We page
// through it (?page=0,1,2… until a page yields no cards), follow each
// /see-and-do/<slug> card to its detail page, and keep only the items a baby
// or toddler could actually attend.
//
// Relevance is decided on the detail page, not the listing card: the listing
// blurb is marketing copy ("a new family show") whereas the detail page's
// <meta name="description"> and its Age info-block field carry the concrete
// age ("under 7s", "Children aged 8 and under"). TextParsing.ParseAgeRange
// turns those into months; we admit a card when the parsed minimum age is
// toddler-or-below (<= 36 months). Items with no age signal are dropped rather
// than guessed at — that's what keeps adult lates and grown-up exhibitions out.
//
// Dates live in the detail page's c-info-block "Date:" field as free text in a
// handful of shapes:
//   • "Open daily, 10.00–17.40"          → a drop-in gallery; one row per day
//                                           across the horizon at the published
//                                           opening time (Pattern Pod, Wonderlab).
//   • "Friday 5 June 2026"               → a single dated occurrence.
//   • "23 October 2026 – 25 April 2027",
//     "From Saturday 23 May 2026",
//     "Now open until 8 September 2026"  → a run; one row per day across the
//                                           in-horizon span.
//   • "Dates vary" / "Wednesdays" / ""   → not datable from the HTML; skipped.
//
// Per-session times for the ticketed shows (e.g. Bubble Explorers, "Show times
// vary so please select your preferred timeslot when booking") are only
// available inside the Tessitura booking calendar, which sits behind a Queue-it
// waiting room + Incapsula WAF and renders entirely client-side — the same wall
// that parked NHM Adventure Babies. When we can't read a real clock time we fall
// back to a 10:00 placeholder, flag TimeApproximate, and note in SessionNotes
// that the visitor should check the booking page. Galleries publish real opening
// hours, so those are exact.
public sealed class ScienceMuseumChildrensScraper : IScraper
{
    private const string Origin = "https://www.sciencemuseum.org.uk";
    private const string ListingPath = "/see-and-do";
    private const string Venue = "Science Museum";
    private const string Address = "Exhibition Road, South Kensington, London";
    private const string Postcode = "SW7 2DD";

    // Safety cap on pagination — the index is ~4 pages today; we stop early on
    // the first empty page, this just bounds a runaway loop if the markup shifts.
    private const int MaxPages = 20;

    // A baby/toddler can attend when the lower age bound is at most this many
    // months. Bubble Explorers ("ages 7 and under") and Pattern Pod ("aged 8
    // and under") both resolve to a minimum of 0; an "8–15 year-olds" gallery
    // resolves to 96 and is excluded.
    private const int ToddlerCeilingMonths = 36;

    // Placeholder when a show hides its times behind the booking system.
    private static readonly TimeOnly PlaceholderStart = new(10, 0);

    public string SourceId => "science_museum_childrens";
    public string Category => Categories.Museum;

    private readonly HttpClient _http;
    private readonly ILogger<ScienceMuseumChildrensScraper> _logger;

    public ScienceMuseumChildrensScraper(HttpClient http, ILogger<ScienceMuseumChildrensScraper> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<IReadOnlyList<EventOccurrence>> ScrapeAsync(int horizonDays, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var horizonEnd = today.AddDays(horizonDays);
        var now = DateTimeOffset.UtcNow;

        var slugs = await CollectSeeAndDoSlugsAsync(ct);
        _logger.LogInformation("Science Museum: {Count} distinct see-and-do items to inspect", slugs.Count);

        var rows = new List<EventOccurrence>();
        foreach (var slug in slugs)
        {
            ct.ThrowIfCancellationRequested();
            var url = $"{Origin}{ListingPath}/{slug}";
            var html = await TryFetchAsync(url, ct);
            if (html is null) continue;

            var doc = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html), ct);
            rows.AddRange(BuildRows(slug, url, doc, today, horizonEnd, now));
        }

        // Galleries appear on the index more than once; ExternalKey is unique.
        return rows
            .GroupBy(r => r.ExternalKey)
            .Select(g => g.First())
            .ToList();
    }

    // Walk the paged index, returning the distinct /see-and-do/<slug> targets in
    // listing order. Non-see-and-do cards (IMAX, afternoon tea, season hubs) are
    // ignored — they're never baby/toddler sessions.
    private async Task<IReadOnlyList<string>> CollectSeeAndDoSlugsAsync(CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();

        for (var page = 0; page < MaxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var html = await TryFetchAsync($"{Origin}{ListingPath}?page={page}", ct);
            if (html is null) break;

            var doc = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html), ct);
            var cards = doc.QuerySelectorAll("article.c-card a.c-card__link");
            if (cards.Length == 0) break; // first empty page → past the end

            foreach (var a in cards)
            {
                var slug = ExtractSeeAndDoSlug(a.GetAttribute("href"));
                if (slug is not null && seen.Add(slug)) ordered.Add(slug);
            }
        }
        return ordered;
    }

    // "/see-and-do/pattern-pod" → "pattern-pod"; anything else → null.
    private static string? ExtractSeeAndDoSlug(string? href)
    {
        if (string.IsNullOrEmpty(href)) return null;
        var m = Regex.Match(href, @"^/see-and-do/([a-z0-9][a-z0-9-]*)/?$", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private IEnumerable<EventOccurrence> BuildRows(
        string slug, string url, IDocument doc, DateOnly today, DateOnly horizonEnd, DateTimeOffset now)
    {
        var title = doc.QuerySelector("h1")?.TextContent?.Trim();
        if (string.IsNullOrEmpty(title)) yield break;

        var info = ReadInfoBlock(doc);
        var metaDesc = doc.QuerySelector("meta[name='description']")?.GetAttribute("content")?.Trim() ?? "";

        // Age comes from the most explicit source available, in priority order:
        // the dedicated Age field, then the meta description, then the title.
        info.TryGetValue("age", out var ageField);
        var (minAge, maxAge) = TextParsing.ParseAgeRange(
            $"{ageField} {metaDesc} {title}");

        // Gate: only items a baby/toddler could attend. No age signal → drop.
        if (minAge is null || minAge > ToddlerCeilingMonths)
        {
            _logger.LogDebug("Science Museum: skipping {Slug} (age min={Min})", slug, minAge);
            yield break;
        }

        info.TryGetValue("date", out var dateField);
        info.TryGetValue("time", out var timeField);
        info.TryGetValue("price", out var priceField);
        info.TryGetValue("location", out var locationField);

        var schedule = ParseSchedule(dateField, timeField, today, horizonEnd);
        if (schedule is null)
        {
            _logger.LogDebug("Science Museum: {Slug} has no datable schedule (\"{Date}\")", slug, dateField);
            yield break;
        }

        var (isFree, cost) = ResolvePrice(priceField, info.GetValueOrDefault("title-label"));

        var notes = BuildNotes(metaDesc, locationField, schedule.Value.Approximate);

        foreach (var date in schedule.Value.Dates)
        {
            if (date < today || date > horizonEnd) continue;
            yield return new EventOccurrence
            {
                ExternalKey = $"{SourceId}:{slug}:{date:yyyy-MM-dd}:{schedule.Value.Start:HHmm}",
                Source = SourceId,
                Category = Category,
                SourceUrl = url,
                Date = date,
                StartTime = schedule.Value.Start,
                EndTime = schedule.Value.End,
                TimeApproximate = schedule.Value.Approximate,
                SessionName = title,
                SessionNotes = notes,
                VenueName = Venue,
                VenueAddress = Address,
                Postcode = Postcode,
                MinAgeMonths = minAge,
                MaxAgeMonths = maxAge,
                TermTimeOnly = false,
                IsFree = isFree,
                Cost = cost,
                LastSeenAt = now,
            };
        }
    }

    // Pulls the c-info-block label/value rows into a dictionary keyed by the
    // lowercased label ("date", "time", "price", "location", "age"). The block's
    // heading ("Free  Interactive gallery" / "Event") is stored under
    // "title-label" so ParsePrice can see a bare "Free".
    private static Dictionary<string, string> ReadInfoBlock(IDocument doc)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var block = doc.QuerySelector("div.c-info-block");
        if (block is null) return map;

        var heading = block.QuerySelector("li.c-info-block__title");
        if (heading is not null) map["title-label"] = Collapse(heading.TextContent);

        foreach (var li in block.QuerySelectorAll("li.c-info-block__section li"))
        {
            var label = li.QuerySelector("span.o-label")?.TextContent?.Trim();
            if (string.IsNullOrEmpty(label)) continue;
            var key = label.TrimEnd(':').Trim().ToLowerInvariant();

            // Value = the li's text with the label prefix removed.
            var full = Collapse(li.TextContent);
            var value = full.StartsWith(label, StringComparison.OrdinalIgnoreCase)
                ? full[label.Length..].Trim()
                : full;
            if (!map.ContainsKey(key)) map[key] = value;
        }
        return map;
    }

    private readonly record struct Schedule(
        IReadOnlyList<DateOnly> Dates, TimeOnly Start, TimeOnly? End, bool Approximate);

    // Turns the free-text Date (+ Time) fields into concrete dates and a start/
    // end time. Returns null when nothing datable can be recovered.
    private static Schedule? ParseSchedule(string? dateText, string? timeText, DateOnly today, DateOnly horizonEnd)
    {
        if (string.IsNullOrWhiteSpace(dateText)) return null;
        var text = dateText.Replace('–', '-').Replace('—', '-').Trim();

        // 1) Drop-in gallery: "Open daily, 10.00–17.40" (times optional).
        if (Regex.IsMatch(text, @"\bopen daily\b", RegexOptions.IgnoreCase))
        {
            var hours = Regex.Match(text, @"(\d{1,2})[.:](\d{2})\s*-\s*(\d{1,2})[.:](\d{2})");
            TimeOnly start;
            TimeOnly? end;
            bool approx;
            if (hours.Success)
            {
                start = new TimeOnly(int.Parse(hours.Groups[1].Value), int.Parse(hours.Groups[2].Value));
                end = new TimeOnly(int.Parse(hours.Groups[3].Value), int.Parse(hours.Groups[4].Value));
                approx = false;
            }
            else
            {
                (start, end, approx) = ResolveTime(timeText);
            }
            return new Schedule(EveryDay(today, horizonEnd), start, end, approx);
        }

        // 2) Explicit calendar dates: "5 June 2026", optionally weekday-prefixed.
        var dates = ExtractDates(text);

        var (tStart, tEnd, tApprox) = ResolveTime(timeText);

        if (dates.Count == 0) return null; // "Dates vary", "Wednesdays", "" → not datable

        DateOnly spanStart, spanEnd;
        bool isRun;
        if (dates.Count >= 2)
        {
            // "23 October 2026 – 25 April 2027" → a continuous run.
            spanStart = dates.First();
            spanEnd = dates.Last();
            isRun = true;
        }
        else
        {
            var only = dates[0];
            if (Regex.IsMatch(text, @"\bfrom\b", RegexOptions.IgnoreCase))
            {
                // "From Saturday 23 May 2026" → open-ended run to the horizon.
                spanStart = only;
                spanEnd = horizonEnd;
                isRun = true;
            }
            else if (Regex.IsMatch(text, @"\buntil\b", RegexOptions.IgnoreCase))
            {
                // "Now open until 8 September 2026" → already running, ends on date.
                spanStart = today;
                spanEnd = only;
                isRun = true;
            }
            else
            {
                spanStart = spanEnd = only; // a single dated occurrence
                isRun = false;
            }
        }

        // Clamp to the horizon.
        if (spanStart < today) spanStart = today;
        if (spanEnd > horizonEnd) spanEnd = horizonEnd;
        if (spanEnd < spanStart) return null; // wholly in the past / outside horizon

        var outDates = isRun ? EveryDay(spanStart, spanEnd) : new[] { spanStart };
        return new Schedule(outDates, tStart, tEnd, tApprox);
    }

    // Pulls calendar dates out of the Date field. Handles the abbreviated UK
    // ranges where an endpoint borrows the other's month/year — "6 - 7 June
    // 2026", "6 June - 7 July 2026" — as well as fully-qualified single dates
    // and two-ended ranges. Returns the dates ascending (a range → its two
    // endpoints; the caller fills the days between).
    private static List<DateOnly> ExtractDates(string text)
    {
        // "6 June - 7 July 2026" (optionally weekday-prefixed each side, as in
        // "Saturday 6 June – Sunday 7 July 2026"): the first endpoint lacks a
        // year, shared with the second. (?<!\d) stops a day number matching the
        // tail of a 4-digit year. Excludes the both-years case (a year sits
        // before the dash), which the generic scan below handles as two tokens.
        var crossMonth = Regex.Match(text,
            @"(?<!\d)(\d{1,2})\s+([A-Za-z]+)\s*-\s*(?:[A-Za-z]+\s+)?(\d{1,2})\s+([A-Za-z]+)\s+(\d{4})");
        if (crossMonth.Success)
        {
            var year = crossMonth.Groups[5].Value;
            var a = TryDate(crossMonth.Groups[1].Value, crossMonth.Groups[2].Value, year);
            var b = TryDate(crossMonth.Groups[3].Value, crossMonth.Groups[4].Value, year);
            if (a is not null && b is not null) return Pair(a.Value, b.Value);
        }

        // "6 - 7 June 2026" / "Saturday 6 – Sunday 7 June 2026": both endpoints
        // share the trailing month and year; a weekday may sit before each day.
        var sameMonth = Regex.Match(text,
            @"(?<!\d)(\d{1,2})\s*-\s*(?:[A-Za-z]+\s+)?(\d{1,2})\s+([A-Za-z]+)\s+(\d{4})");
        if (sameMonth.Success)
        {
            var month = sameMonth.Groups[3].Value;
            var year = sameMonth.Groups[4].Value;
            var a = TryDate(sameMonth.Groups[1].Value, month, year);
            var b = TryDate(sameMonth.Groups[2].Value, month, year);
            if (a is not null && b is not null) return Pair(a.Value, b.Value);
        }

        // Generic: every fully-qualified "D Month YYYY" in the field.
        return Regex.Matches(text, @"(?<!\d)(\d{1,2})\s+([A-Za-z]+)\s+(\d{4})")
            .Select(m => TryDate(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value))
            .Where(d => d is not null)
            .Select(d => d!.Value)
            .OrderBy(d => d)
            .ToList();
    }

    private static List<DateOnly> Pair(DateOnly a, DateOnly b) =>
        a <= b ? new List<DateOnly> { a, b } : new List<DateOnly> { b, a };

    // Price intent for a Science Museum item. A printed £ amount always wins,
    // even alongside a conditional concession ("£4.50 per person. Ages 2 and
    // under go free") — TextParsing.ParsePrice can't tell that "free" from a
    // genuinely free event, so we resolve it here. Only when there's no amount
    // do we trust a standalone "Free" (galleries label themselves that way).
    private static (bool isFree, decimal? cost) ResolvePrice(string? priceField, string? titleLabel)
    {
        decimal? min = null;
        foreach (Match m in Regex.Matches(priceField ?? "", @"£\s*(\d+(?:\.\d{1,2})?)"))
        {
            if (decimal.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
            {
                if (min is null || v < min) min = v;
            }
        }
        if (min is not null) return (false, min);

        if (Regex.IsMatch($"{priceField} {titleLabel}", @"\bfree\b", RegexOptions.IgnoreCase))
            return (true, null);
        return (false, null);
    }

    // Reads a start (and optional end) clock time from the Time field. When none
    // is published ("Show times vary…") we return a placeholder flagged approximate.
    private static (TimeOnly start, TimeOnly? end, bool approx) ResolveTime(string? timeText)
    {
        if (!string.IsNullOrWhiteSpace(timeText))
        {
            var clocks = Regex.Matches(timeText, @"(\d{1,2})[.:](\d{2})")
                .Select(m => new TimeOnly(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)))
                .ToList();
            if (clocks.Count >= 2) return (clocks[0], clocks[1], false);
            if (clocks.Count == 1) return (clocks[0], null, false);
        }
        return (PlaceholderStart, null, true);
    }

    private static IReadOnlyList<DateOnly> EveryDay(DateOnly from, DateOnly to)
    {
        var list = new List<DateOnly>();
        for (var d = from; d <= to; d = d.AddDays(1)) list.Add(d);
        return list;
    }

    private static DateOnly? TryDate(string day, string month, string year)
    {
        if (!int.TryParse(day, out var d) || !int.TryParse(year, out var y)) return null;
        if (ParseMonth(month) is not int mo) return null;
        try { return new DateOnly(y, mo, d); }
        catch (ArgumentOutOfRangeException) { return null; }
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

    private static string? BuildNotes(string? metaDesc, string? location, bool approximate)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(metaDesc)) parts.Add(metaDesc.Trim());
        if (!string.IsNullOrWhiteSpace(location)) parts.Add($"Location: {location.Trim()}.");
        if (approximate)
            parts.Add("Session times vary — check the Science Museum booking page for exact dates and times.");
        if (parts.Count == 0) return null;
        return Truncate(string.Join(" ", parts), 400);
    }

    private async Task<string?> TryFetchAsync(string url, CancellationToken ct)
    {
        try { return await _http.GetStringAsync(url, ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Science Museum: fetch of {Url} failed", url);
            return null;
        }
    }

    private static string Collapse(string s) =>
        Regex.Replace(s, @"\s+", " ").Trim();

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";
}
