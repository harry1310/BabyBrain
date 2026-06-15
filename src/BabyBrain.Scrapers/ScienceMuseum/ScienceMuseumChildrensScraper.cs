using System.Text;
using System.Text.Json;
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
// Ticketed shows (e.g. Bubble Explorers) only say "Show times vary…" on the
// detail page — the real dates and times live in the Tessitura booking system.
// Its calendar renders client-side behind a Queue-it waiting room, BUT the JSON
// feed it pulls from — POST /api/products/productionseasons on
// my.sciencemuseum.org.uk — is reachable directly: anonymous, no cookies, no
// queue token, given the public mode-of-sale/source ids and the event's keyword
// id (the `kid` in its "Book now" link). So when a detail page carries a booking
// kid we ask that feed for the genuine performances and emit one exact, real-time
// row each; only if the feed is unreachable or empty do we fall back to parsing
// the human-readable Date field (flagging TimeApproximate when no time shows).
// Galleries (Pattern Pod etc.) have no booking kid and publish real opening
// hours, so those stay exact too.
public sealed class ScienceMuseumChildrensScraper : IScraper
{
    private const string Origin = "https://www.sciencemuseum.org.uk";
    private const string ListingPath = "/see-and-do";
    private const string Venue = "Science Museum";
    private const string Address = "Exhibition Road, South Kensington, London";
    private const string Postcode = "SW7 2DD";

    // Tessitura booking feed: the public-web mode-of-sale (3) and source (1) are
    // seeded into every booking page ("Session MOS: 3", sourceId 1) and let us
    // POST for a keyword's performances anonymously.
    private const string BookingApiUrl = "https://my.sciencemuseum.org.uk/api/products/productionseasons";
    private const int BookingModeOfSale = 3;
    private const int BookingSourceId = 1;

    // Safety cap on pagination — the index is ~4 pages today; we stop early on
    // the first empty page, this just bounds a runaway loop if the markup shifts.
    private const int MaxPages = 20;

    // A baby/toddler can attend when the lower age bound is at most this many
    // months. Bubble Explorers ("ages 7 and under") and Pattern Pod ("aged 8
    // and under") both resolve to a minimum of 0; an "8–15 year-olds" gallery
    // resolves to 96 and is excluded.
    private const int ToddlerCeilingMonths = 36;

    // …and we additionally require the upper bound to sit within young-childhood
    // (<= 9 years). This keeps the genuinely little-one offerings (Bubble's "7
    // and under" → max 84, Pattern Pod's "8 and under" → max 96) while dropping
    // broad whole-family items like the Great Exhibition Road Festival
    // ("under 12s and their families" → max 144) that aren't baby/toddler events.
    private const int YoungChildCeilingMonths = 9 * 12;

    // Placeholder when a fallback can't read a real clock time.
    private static readonly TimeOnly PlaceholderStart = new(10, 0);

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public string SourceId => "science_museum_childrens";
    public string Category => Categories.Museum;

    // see-and-do pages sit behind Cloudflare, which now 403s the VPS datacenter
    // IP regardless of UA (worked over plain HTTP until mid-June 2026). Those GETs
    // go through the shared fetcher (laptop Chrome → ScraperAPI, + cache) like the
    // British Museum / Southbank scrapers. The Tessitura booking feed lives on a
    // different, un-blocked host (my.sciencemuseum.org.uk) so it stays on _http.
    private readonly IContentFetcher _fetcher;
    private readonly HttpClient _http;
    private readonly ILogger<ScienceMuseumChildrensScraper> _logger;

    public ScienceMuseumChildrensScraper(
        IContentFetcher fetcher, HttpClient http, ILogger<ScienceMuseumChildrensScraper> logger)
    {
        _fetcher = fetcher;
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
            var html = await TryFetchAsync(url, CacheTtl.Detail, ct);
            if (html is null) continue;

            var doc = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html), ct);
            rows.AddRange(await BuildRowsAsync(slug, url, doc, today, horizonEnd, now, ct));
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
            var html = await TryFetchAsync($"{Origin}{ListingPath}?page={page}", CacheTtl.Listing, ct);
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

    private async Task<IReadOnlyList<EventOccurrence>> BuildRowsAsync(
        string slug, string url, IDocument doc, DateOnly today, DateOnly horizonEnd, DateTimeOffset now,
        CancellationToken ct)
    {
        var title = doc.QuerySelector("h1")?.TextContent?.Trim();
        if (string.IsNullOrEmpty(title)) return Array.Empty<EventOccurrence>();

        var info = ReadInfoBlock(doc);
        var metaDesc = doc.QuerySelector("meta[name='description']")?.GetAttribute("content")?.Trim() ?? "";

        // Age comes from the most explicit source available, in priority order:
        // the dedicated Age field, then the meta description, then the title.
        info.TryGetValue("age", out var ageField);
        var (minAge, maxAge) = TextParsing.ParseAgeRange(
            $"{ageField} {metaDesc} {title}");

        // Gate: keep only genuinely young-child items — a toddler-or-below lower
        // bound AND (where stated) an upper bound within young-childhood. No age
        // signal → drop.
        if (minAge is null || minAge > ToddlerCeilingMonths || maxAge > YoungChildCeilingMonths)
        {
            _logger.LogDebug("Science Museum: skipping {Slug} (age {Min}-{Max})", slug, minAge, maxAge);
            return Array.Empty<EventOccurrence>();
        }

        info.TryGetValue("date", out var dateField);
        info.TryGetValue("time", out var timeField);
        info.TryGetValue("price", out var priceField);
        info.TryGetValue("location", out var locationField);

        var (isFree, cost) = ResolvePrice(priceField, info.GetValueOrDefault("title-label"));

        EventOccurrence Row(DateOnly date, TimeOnly start, TimeOnly? end, bool approx, string? notes) => new()
        {
            ExternalKey = $"{SourceId}:{slug}:{date:yyyy-MM-dd}:{start:HHmm}",
            Source = SourceId,
            Category = Category,
            SourceUrl = url,
            Date = date,
            StartTime = start,
            EndTime = end,
            TimeApproximate = approx,
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

        // Preferred path: a booking kid → real performances from the Tessitura feed.
        if (ExtractBookingKid(doc) is int kid)
        {
            var perfs = await FetchBookingPerformancesAsync(kid, today, horizonEnd, ct);
            var inHorizon = perfs.Where(p => p.Date >= today && p.Date <= horizonEnd).ToList();
            if (inHorizon.Count > 0)
            {
                var notes = BuildNotes(metaDesc, locationField, approximate: false);
                return inHorizon.Select(p => Row(p.Date, p.Time, null, approx: false, notes)).ToList();
            }
            _logger.LogDebug("Science Museum: {Slug} kid={Kid} returned no in-horizon performances; " +
                "falling back to the Date field", slug, kid);
        }

        // Fallback: parse the human-readable Date field (galleries, un-ticketed dates).
        var schedule = ParseSchedule(dateField, timeField, today, horizonEnd);
        if (schedule is null)
        {
            _logger.LogDebug("Science Museum: {Slug} has no datable schedule (\"{Date}\")", slug, dateField);
            return Array.Empty<EventOccurrence>();
        }

        var fallbackNotes = BuildNotes(metaDesc, locationField, schedule.Value.Approximate);
        return schedule.Value.Dates
            .Where(d => d >= today && d <= horizonEnd)
            .Select(d => Row(d, schedule.Value.Start, schedule.Value.End, schedule.Value.Approximate, fallbackNotes))
            .ToList();
    }

    // The Tessitura keyword id behind a detail page's "Book now" CTA. Scoped to
    // the c-info-block so we get the event's own booking link, not the general
    // "Book your free admission" banner (a different kid) elsewhere on the page.
    private static int? ExtractBookingKid(IDocument doc)
    {
        var block = doc.QuerySelector("div.c-info-block");
        var href = block?.QuerySelector("a[href*='kid=']")?.GetAttribute("href");
        var m = Regex.Match(href ?? "", @"[?&]kid=(\d+)");
        return m.Success && int.TryParse(m.Groups[1].Value, out var kid) ? kid : null;
    }

    // POSTs the public booking feed for a keyword's performances and returns each
    // as a local date + clock time. Best-effort: any failure yields an empty list
    // so the caller falls back to the Date field rather than dropping the event.
    private async Task<IReadOnlyList<(DateOnly Date, TimeOnly Time)>> FetchBookingPerformancesAsync(
        int kid, DateOnly from, DateOnly to, CancellationToken ct)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                modeOfSale = BookingModeOfSale,
                sourceId = BookingSourceId,
                keywordIds = new[] { kid },
                // DateOnly.ToString rejects time specifiers, so append the literal.
                startDate = from.ToString("yyyy-MM-dd") + "T00:00",
                endDate = to.AddDays(1).ToString("yyyy-MM-dd") + "T00:00",
            });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(BookingApiUrl, content, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Science Museum: booking feed for kid={Kid} returned {Status}", kid, (int)resp.StatusCode);
                return Array.Empty<(DateOnly, TimeOnly)>();
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            var seasons = JsonSerializer.Deserialize<List<ProductionSeason>>(json, JsonOpts) ?? new();

            var result = new List<(DateOnly, TimeOnly)>();
            foreach (var perf in seasons.SelectMany(s => s.Performances ?? new()))
            {
                if (!perf.IsPerformanceVisible) continue;
                if (TryParseLocalDateTime(perf.Iso8601DateString, out var d, out var t))
                    result.Add((d, t));
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Science Museum: booking feed for kid={Kid} failed", kid);
            return Array.Empty<(DateOnly, TimeOnly)>();
        }
    }

    // "2026-06-06T10:00:00.0000000+01:00" → 2026-06-06, 10:00. We take the local
    // wall-clock components verbatim (the offset already reflects BST/GMT).
    private static bool TryParseLocalDateTime(string? iso, out DateOnly date, out TimeOnly time)
    {
        date = default;
        time = default;
        if (string.IsNullOrEmpty(iso) || iso.Length < 16 || iso[10] != 'T') return false;
        return DateOnly.TryParse(iso[..10], out date)
            && TimeOnly.TryParseExact(iso.Substring(11, 5), "HH:mm", out time);
    }

    private sealed record ProductionSeason
    {
        public List<Performance>? Performances { get; init; }
    }

    private sealed record Performance
    {
        public string? Iso8601DateString { get; init; }
        public bool IsPerformanceVisible { get; init; }
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

    // Fetch a see-and-do page through the shared fetcher (laptop Chrome →
    // ScraperAPI, cached for `ttl`). Credits-exhausted propagates so the runner
    // reports the source as blocked rather than a silent zero; any other failure
    // returns null and the caller skips/stops.
    private async Task<string?> TryFetchAsync(string url, TimeSpan ttl, CancellationToken ct)
    {
        try { return await _fetcher.FetchAsync(SourceId, url, ttl, ct: ct); }
        catch (ScraperApiCreditsExhaustedException) { throw; }
        catch (Exception ex)
        {
            ct.ThrowIfCancellationRequested();
            _logger.LogWarning(ex, "Science Museum: fetch of {Url} failed", url);
            return null;
        }
    }

    private static string Collapse(string s) =>
        Regex.Replace(s, @"\s+", " ").Trim();

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";
}
