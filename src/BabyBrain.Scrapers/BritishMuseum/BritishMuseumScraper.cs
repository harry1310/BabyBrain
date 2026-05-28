using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using BabyBrain.Scrapers.Domain;
using BabyBrain.Scrapers.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

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

    // Detail-page renders occasionally time out transiently on the small
    // production VPS. Retry before giving up — a swallowed miss silently
    // drops a whole event (this is what made the live row count lurch to 2).
    // Bumped from 2 to 3 after issue #10: a single teaser timed out x2 on the
    // same run, and the carousel only had 2 cards that day (the other was
    // age-filtered), so the run produced 0 rows.
    private const int TeaserFetchAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    // The BM detail pages JS-render their occurrence list. That render is ~5s
    // locally but exceeds the default 30s wait on the 2-vCPU production VPS
    // (issue #7: both detail fetches timed out at 30s; issue #10: 90s wasn't
    // enough either when the detail page weighed ~4.7MB). Give it a generous
    // ceiling — slow is fine for a daily background scrape; failing isn't.
    private const int DetailWaitMs = 120_000;

    // BabyBrain covers under-5s. The BM "Family events" carousel also lists
    // school-age activities; an event whose stated minimum age is at or above
    // this (5 years, in months) is dropped.
    private const int UnderFiveCutoffMonths = 60;

    public string SourceId => "british_museum_family";
    public string Category => Categories.Museum;

    private readonly PlaywrightFetcher _fetcher;
    private readonly CurlFetcher _curl;
    private readonly ILogger<BritishMuseumScraper> _logger;

    public BritishMuseumScraper(PlaywrightFetcher fetcher, CurlFetcher curl, ILogger<BritishMuseumScraper> logger)
    {
        _fetcher = fetcher;
        _curl = curl;
        _logger = logger;
    }

    public async Task<IReadOnlyList<EventOccurrence>> ScrapeAsync(int horizonDays, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var horizonEnd = today.AddDays(horizonDays);
        var now = DateTimeOffset.UtcNow;
        var rows = new List<EventOccurrence>();
        var diag = new StringBuilder();

        // Hub page: pull event card links from the #family-events section.
        // The hub server-side renders when given a browser User-Agent — every
        // teaser anchor is in the response body, no JS needed. We use
        // CurlFetcher (not Playwright, not HttpClient): Playwright kept
        // timing out on the slow VPS, and HttpClient is 403'd by Cloudflare
        // because of TLS fingerprinting. curl gets through cleanly.
        string hubHtml;
        try
        {
            hubHtml = await _curl.FetchAsync(HubUrl, ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"British Museum hub fetch failed: {ex.GetType().Name}: {ex.Message}", ex);
        }

        var hub = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(hubHtml), ct);
        var teasers = ExtractTeasers(hub).ToList();
        diag.Append($"hub {hubHtml.Length} chars, {teasers.Count} teaser(s). ");

        foreach (var teaser in teasers)
        {
            ct.ThrowIfCancellationRequested();

            // Retry the detail fetch: a transient render timeout shouldn't cost
            // us the whole event. Wait for an actual occurrence row, not just
            // the [data-js-event-occurrences] container — that shell attaches
            // before its accordion rows render in, and snapshotting the gap
            // yields an empty parse. Attached, not Visible: the rows sit inside
            // collapsed accordions, so they're in the DOM but not painted.
            Exception? teaserFailure = null;
            for (var attempt = 1; attempt <= TeaserFetchAttempts; attempt++)
            {
                try
                {
                    var sw = Stopwatch.StartNew();
                    var detailHtml = await _fetcher.FetchRenderedHtmlAsync(
                        teaser.Url,
                        "[data-js-event-occurrences] .occurrence-list__item",
                        WaitForSelectorState.Attached,
                        ct,
                        selectorTimeoutMs: DetailWaitMs);
                    sw.Stop();
                    var detail = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(detailHtml), ct);
                    var (teaserRows, note) = BuildOccurrences(detail, teaser, today, horizonEnd, now);
                    rows.AddRange(teaserRows);
                    diag.Append($"[{teaser.Title}] OK in {sw.ElapsedMilliseconds}ms " +
                                $"({detailHtml.Length} chars): {note}. ");
                    teaserFailure = null;
                    break;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    teaserFailure = ex;
                    if (attempt < TeaserFetchAttempts)
                        await Task.Delay(RetryDelay, ct);
                }
            }
            if (teaserFailure is not null)
                diag.Append($"[{teaser.Title}] FETCH FAILED x{TeaserFetchAttempts}: " +
                            $"{teaserFailure.GetType().Name}: {teaserFailure.Message}. ");
        }

        var summary = $"British Museum scrape: {rows.Count} row(s). {diag}".TrimEnd();

        // 0 rows is treated as a failure by the orchestrator anyway; throw with
        // the full breakdown so the *reason* reaches the GitHub issue, not just
        // a bare "returned 0 events".
        if (rows.Count == 0)
            throw new InvalidOperationException(summary);

        _logger.LogInformation("{Summary}", summary);
        return rows;
    }

    private record Teaser(string Title, string Summary, string Url);

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
    private (List<EventOccurrence> Rows, string Note) BuildOccurrences(
        IDocument detail, Teaser teaser, DateOnly from, DateOnly to, DateTimeOffset now)
    {
        var rows = new List<EventOccurrence>();

        var container = detail.QuerySelector("[data-js-event-occurrences]");
        if (container is null)
            return (rows, "no [data-js-event-occurrences] container");

        var vid = container.GetAttribute("data-vid") ?? Slug(teaser.Url);
        var (minAge, maxAge) = TextParsing.ParseAgeRange(teaser.Summary);

        // Skip school-age events (e.g. the 8-15 sleepover). An event with no
        // stated age is kept — most BM family activities welcome all ages.
        if (minAge is int min && min >= UnderFiveCutoffMonths)
            return (rows, $"age-filtered ({min}mo+, school-age)");

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
        return (rows, note);
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
