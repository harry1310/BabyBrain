using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using BabyBrain.Scrapers.Domain;
using BabyBrain.Scrapers.Shared;
using Microsoft.Extensions.Logging;

namespace BabyBrain.Scrapers.BritishMuseum;

// Source: https://www.britishmuseum.org/visit/family-visits
// The "Family events" section on the hub page is a 3-item carousel of
// curated upcoming family activities. Each card links to /events/<slug>; the
// detail page renders the actual dates and times into an occurrence-list
// accordion. We walk hub → detail → emit one row per (date, start time).
//
// This scraper has been flaky on the small production VPS, so it carries
// verbose diagnostics: every run logs a per-teaser breakdown, and a run that
// ends with 0 rows throws with that same breakdown as the message — which
// lands in the scrape-failure GitHub issue, where it can actually be read.
public sealed class BritishMuseumScraper : IScraper
{
    private const string HubUrl = "https://www.britishmuseum.org/visit/family-visits";
    private const string Origin = "https://www.britishmuseum.org";
    private const string Venue = "British Museum";
    private const string Address = "Great Russell Street, London";
    private const string Postcode = "WC1B 3DG";

    // BabyBrain covers under-5s. The BM "Family events" carousel also lists
    // school-age activities; an event whose stated minimum age is at or above
    // this (5 years, in months) is dropped.
    private const int UnderFiveCutoffMonths = 60;

    // BM detail pages drop their age guidance inline with each activity
    // (`<br>Ages 6+<br>`, `<br>Ages 5 and under<br>`) rather than in a
    // structured "Age guidance" item like SBC. An event with several
    // activities can carry several different age signals — this regex finds
    // every "Ages …" phrase so we can take the most inclusive view.
    private static readonly Regex AgeGuidancePattern = new(
        @"[Aa]ges?\s+(?:\d+\+|\d+\s*(?:and|or)\s*under|\d+\s*[-–—]\s*\d+)",
        RegexOptions.Compiled);

    public string SourceId => "british_museum_family";
    public string Category => Categories.Museum;

    private readonly IContentFetcher _fetcher;
    private readonly ILogger<BritishMuseumScraper> _logger;

    public BritishMuseumScraper(IContentFetcher fetcher, ILogger<BritishMuseumScraper> logger)
    {
        _fetcher = fetcher;
        _logger = logger;
    }

    public async Task<IReadOnlyList<EventOccurrence>> ScrapeAsync(int horizonDays, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var horizonEnd = today.AddDays(horizonDays);
        var now = DateTimeOffset.UtcNow;
        var rows = new List<EventOccurrence>();
        var diag = new StringBuilder();

        // Hub + detail both go via ScraperAPI's residential proxies — Cloudflare
        // 403s every request from the Hetzner VPS (and from CF Workers too) even
        // with a browser-shaped client (issues #18, #21). Detail uses renderJs=true
        // because the occurrence list is filled in client-side; the same render
        // also lets us read the inline "Ages …" guidance that BM weaves into each
        // activity description (issue surfaced by the 6+ Spring myths event
        // slipping through after #22).
        string hubHtml;
        try
        {
            hubHtml = await _fetcher.FetchAsync(SourceId, HubUrl, CacheTtl.Listing, ct: ct);
        }
        catch (ScraperApiCreditsExhaustedException) { throw; } // billing state — let the runner mark it blocked
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"British Museum hub fetch failed: {ex.GetType().Name}: {ex.Message}", ex);
        }

        var hub = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(hubHtml), ct);
        var teasers = ExtractTeasers(hub).ToList();
        diag.Append($"hub {hubHtml.Length} chars, {teasers.Count} teaser(s). ");

        // Tracks whether any teaser looked *broken* (a failed detail fetch or a
        // page we couldn't parse) as opposed to merely empty for a legitimate
        // reason (age-filtered, or all dates beyond the horizon). This decides
        // whether a final 0-row outcome is a failure or a genuine empty source.
        var anyStructural = false;

        foreach (var teaser in teasers)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var sw = Stopwatch.StartNew();
                var detailHtml = await _fetcher.FetchAsync(SourceId, teaser.Url, CacheTtl.Detail, renderJs: true, ct: ct);
                sw.Stop();
                var detail = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(detailHtml), ct);
                var (teaserRows, note, outcome) = BuildOccurrences(detail, teaser, today, horizonEnd, now);
                rows.AddRange(teaserRows);
                if (outcome == TeaserOutcome.Structural) anyStructural = true;
                diag.Append($"[{teaser.Title}] OK in {sw.ElapsedMilliseconds}ms " +
                            $"({detailHtml.Length} chars): {note}. ");
            }
            catch (OperationCanceledException) { throw; }
            catch (ScraperApiCreditsExhaustedException) { throw; } // billing state — blocks the whole run
            catch (Exception ex)
            {
                anyStructural = true; // a detail fetch that failed is a real fault, not a benign empty
                diag.Append($"[{teaser.Title}] FETCH FAILED: " +
                            $"{ex.GetType().Name}: {ex.Message}. ");
            }
        }

        var summary = $"British Museum scrape: {rows.Count} row(s). {diag}".TrimEnd();

        if (rows.Count > 0)
        {
            _logger.LogInformation("{Summary}", summary);
            return rows;
        }

        // 0 rows. Distinguish a genuine empty source — reached and parsed fine,
        // but every event is school-age or beyond the horizon — from a
        // malfunction (no teasers found at all, a detail fetch failed, or a page
        // format we couldn't parse). Both throw with the full breakdown so the
        // *reason* is preserved, but only the malfunction is a scrape failure;
        // the genuine empty is recorded as a 0-row success by the runner. The BM
        // family hub regularly carries only school-age events (issue #36).
        if (teasers.Count == 0 || anyStructural)
            throw new InvalidOperationException(summary);

        throw new SourceEmptyException(summary);
    }

    private record Teaser(string Title, string Summary, string Url);

    // How one teaser's detail page resolved. Rows = produced occurrences;
    // BenignEmpty = parsed fine but nothing matched (age-filtered, no upcoming
    // dates, or all out of horizon); Structural = a page we couldn't parse
    // (missing occurrence container, or labels/times we failed to read), which
    // signals a likely format change rather than a genuinely empty event.
    private enum TeaserOutcome { Rows, BenignEmpty, Structural }

    private static IEnumerable<Teaser> ExtractTeasers(IDocument hub)
    {
        // The hub section uses an anchor div with id="family-events" followed by a sibling
        // teaser-listing container. Walk every teaser__anchor inside that section's listing.
        var anchor = hub.QuerySelector("#family-events");
        if (anchor is null) yield break;

        // Climb up to the enclosing <section> to scope our teaser search.
        var section = anchor.Closest("section");
        if (section is null) yield break;

        foreach (var a in section.QuerySelectorAll("a.teaser__anchor"))
        {
            var href = a.GetAttribute("href");
            if (string.IsNullOrEmpty(href) || !href.StartsWith("/events/", StringComparison.OrdinalIgnoreCase))
                continue;
            var teaser = a.Closest(".teaser");
            var title = a.QuerySelector(".teaser__title span span")?.TextContent.Trim()
                ?? a.TextContent.Trim();
            var summary = teaser?.QuerySelector(".teaser__summary")?.TextContent.Trim() ?? "";
            yield return new Teaser(title, summary, Origin + href);
        }
    }

    // Builds the rows for one event's detail page, plus a short note describing
    // what was seen — so a 0-row outcome explains itself in the run diagnostics.
    private (List<EventOccurrence> Rows, string Note, TeaserOutcome Outcome) BuildOccurrences(
        IDocument detail, Teaser teaser, DateOnly from, DateOnly to, DateTimeOffset now)
    {
        var rows = new List<EventOccurrence>();

        var container = detail.QuerySelector("[data-js-event-occurrences]");
        if (container is null)
            return (rows, "no [data-js-event-occurrences] container", TeaserOutcome.Structural);

        var vid = container.GetAttribute("data-vid") ?? Slug(teaser.Url);

        // BM weaves age guidance inline ("Ages 6+", "Ages 5 and under") with
        // each activity description. An event might have several activities,
        // each with its own age signal. Keep the event if ANY activity could
        // include under-5s; drop only when every detected age signal is
        // school-age. Fall back to the hub teaser summary when the detail
        // body yields nothing.
        var detailBodyText = detail.QuerySelector("body")?.TextContent ?? "";
        var (minAge, maxAge) = MostInclusiveAge(detailBodyText);
        if (minAge is null && maxAge is null)
            (minAge, maxAge) = TextParsing.ParseAgeRange(teaser.Summary);

        if (minAge is int min && min >= UnderFiveCutoffMonths)
            return (rows, $"age-filtered ({min}mo+, school-age)", TeaserOutcome.BenignEmpty);

        // The accordion is a flat sequence of: <h3 .accordion__heading><button><span>May 2026</span>…
        // followed by <div .accordion__content> containing <dl .occurrence-list>. Months can repeat
        // across an event so we iterate the accordion items in order.
        int accordionItems = 0, occurrences = 0, monthUnparsed = 0, dayUnparsed = 0, outOfHorizon = 0, noTime = 0;
        foreach (var item in container.QuerySelectorAll(".accordion__item"))
        {
            accordionItems++;
            var monthLabel = item.QuerySelector(".accordion__button span")?.TextContent.Trim();
            if (!TryParseMonthYear(monthLabel, out var year, out var month)) { monthUnparsed++; continue; }

            foreach (var occurrence in item.QuerySelectorAll(".occurrence-list__item"))
            {
                occurrences++;
                var dayLabel = occurrence.QuerySelector(".occurrence-list__days")?.TextContent.Trim();
                var day = ParseDayOfMonth(dayLabel);
                if (day is null) { dayUnparsed++; continue; }

                DateOnly date;
                try { date = new DateOnly(year, month, day.Value); }
                catch (ArgumentOutOfRangeException) { dayUnparsed++; continue; }
                if (date < from || date > to) { outOfHorizon++; continue; }

                var before = rows.Count;
                foreach (var timeEl in occurrence.QuerySelectorAll(".occurrence-list__time"))
                {
                    var (start, end) = ParseTimeRange(timeEl.TextContent);
                    if (start is null) continue;

                    rows.Add(new EventOccurrence
                    {
                        ExternalKey = $"{SourceId}:{vid}:{date:yyyy-MM-dd}:{start:HHmm}",
                        Source = SourceId,
                        Category = Category,
                        SourceUrl = teaser.Url,
                        Date = date,
                        StartTime = start.Value,
                        EndTime = end,
                        SessionName = teaser.Title,
                        SessionNotes = teaser.Summary.Length > 0 ? teaser.Summary : null,
                        VenueName = Venue,
                        VenueAddress = Address,
                        Postcode = Postcode,
                        MinAgeMonths = minAge,
                        MaxAgeMonths = maxAge,
                        TermTimeOnly = false,
                        IsFree = true,
                        LastSeenAt = now,
                    });
                }
                if (rows.Count == before) noTime++;
            }
        }

        var note = $"{accordionItems} accordion item(s), {occurrences} occurrence(s) -> {rows.Count} row(s)"
            + (monthUnparsed > 0 ? $"; {monthUnparsed} month-label unparsed" : "")
            + (dayUnparsed > 0 ? $"; {dayUnparsed} day unparsed" : "")
            + (outOfHorizon > 0 ? $"; {outOfHorizon} out-of-horizon" : "")
            + (noTime > 0 ? $"; {noTime} with no parseable time" : "");

        // Rows -> good. Labels/times we couldn't parse -> a likely format change
        // (Structural). Otherwise the event simply has no dates in our window
        // (no accordion items, or everything out of horizon) -> BenignEmpty.
        TeaserOutcome outcome;
        if (rows.Count > 0)
            outcome = TeaserOutcome.Rows;
        else if (monthUnparsed > 0 || dayUnparsed > 0 || noTime > 0)
            outcome = TeaserOutcome.Structural;
        else
            outcome = TeaserOutcome.BenignEmpty;
        return (rows, note, outcome);
    }

    // Walks every "Ages …" phrase in the page text and returns the most
    // inclusive band: lowest min across all matches, and null max if any
    // match is unbounded ("Ages 6+"). Returns (null, null) if no phrase
    // was matched.
    private static (int? min, int? max) MostInclusiveAge(string text)
    {
        int? min = null;
        int? max = null;
        var unboundedAbove = false;
        foreach (Match phrase in AgeGuidancePattern.Matches(text))
        {
            var (parsedMin, parsedMax) = TextParsing.ParseAgeRange(phrase.Value);
            if (parsedMin is int mn && (min is null || mn < min)) min = mn;
            if (parsedMax is null && parsedMin is not null)
                unboundedAbove = true;
            else if (parsedMax is int mx && (max is null || mx > max))
                max = mx;
        }
        if (unboundedAbove) max = null;
        return (min, max);
    }

    private static bool TryParseMonthYear(string? label, out int year, out int month)
    {
        year = 0; month = 0;
        if (string.IsNullOrWhiteSpace(label)) return false;
        // Expect "May 2026", "June 2026", etc.
        var m = Regex.Match(label, @"^(?<m>[A-Za-z]+)\s+(?<y>\d{4})$");
        if (!m.Success) return false;
        if (!DateTime.TryParseExact(m.Groups["m"].Value, "MMMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            && !DateTime.TryParseExact(m.Groups["m"].Value, "MMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
            return false;
        year = int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture);
        month = dt.Month;
        return true;
    }

    private static int? ParseDayOfMonth(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        // "Wednesday 20 May" — pull the bare day number.
        var m = Regex.Match(label, @"\b(\d{1,2})\b");
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    private static (TimeOnly? start, TimeOnly? end) ParseTimeRange(string raw)
    {
        // Format: "10.30–11.15" (en dash). Some entries are single-time.
        var t = raw.Trim();
        var parts = t.Split(new[] { '–', '-', '—' }, 2);
        var start = ParseClockToken(parts[0]);
        TimeOnly? end = parts.Length > 1 ? ParseClockToken(parts[1]) : null;
        return (start, end);
    }

    private static TimeOnly? ParseClockToken(string raw)
    {
        var t = raw.Trim().Replace('.', ':');
        return TimeOnly.TryParseExact(t, new[] { "HH:mm", "H:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var v)
            ? v
            : null;
    }

    private static string Slug(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
}
