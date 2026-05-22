using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using BabyBrain.Scrapers.Domain;
using BabyBrain.Scrapers.Shared;
using Microsoft.Playwright;

namespace BabyBrain.Scrapers.BritishMuseum;

// Source: https://www.britishmuseum.org/visit/family-visits
// The "Family events" section on the hub page is a 3-item carousel of
// curated upcoming family activities. Each card links to /events/<slug>; the
// detail page server-side-renders the actual dates and times into an
// occurrence-list accordion. We walk hub → detail → emit one row per
// (date, start time).
public sealed class BritishMuseumScraper : IScraper
{
    private const string HubUrl = "https://www.britishmuseum.org/visit/family-visits";
    private const string Origin = "https://www.britishmuseum.org";
    private const string Venue = "British Museum";
    private const string Address = "Great Russell Street, London";
    private const string Postcode = "WC1B 3DG";

    // Detail-page renders occasionally time out transiently on the small
    // production VPS. Retry once before giving up — a swallowed miss silently
    // drops a whole event (this is what made the live row count lurch to 2).
    private const int TeaserFetchAttempts = 2;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    public string SourceId => "british_museum_family";
    public string Category => Categories.Museum;

    private readonly PlaywrightFetcher _fetcher;

    public BritishMuseumScraper(PlaywrightFetcher fetcher) => _fetcher = fetcher;

    public async Task<IReadOnlyList<EventOccurrence>> ScrapeAsync(int horizonDays, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var horizonEnd = today.AddDays(horizonDays);
        var now = DateTimeOffset.UtcNow;
        var rows = new List<EventOccurrence>();

        // Hub page: pull event card links from the #family-events section.
        // Wait Attached, not Visible — the carousel anchors are in the DOM
        // before the carousel paints, and we only read the markup. Visible
        // sometimes timed out on the production VPS even though the data
        // was present, leading to a 0-event success.
        var hubHtml = await _fetcher.FetchRenderedHtmlAsync(
            HubUrl,
            "a.teaser__anchor[href^='/events/']",
            WaitForSelectorState.Attached,
            ct);
        var hub = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(hubHtml), ct);
        var teasers = ExtractTeasers(hub).ToList();

        // If every teaser fetch ends in a swallowed timeout we'd return 0 rows
        // with no error — indistinguishable from a real "no events" result.
        // Keep skipping individual non-listing pages, but remember the last
        // exception so we can rethrow if EVERY teaser fetch failed.
        var attempted = 0;
        Exception? lastFailure = null;
        foreach (var teaser in teasers)
        {
            ct.ThrowIfCancellationRequested();
            attempted++;

            // Retry the detail fetch: a transient render timeout shouldn't cost
            // us the whole event. A page that genuinely has no occurrence list
            // (sold-out / draft / festival landing) will fail every attempt —
            // that just wastes one extra fetch, which is the right trade.
            for (var attempt = 1; attempt <= TeaserFetchAttempts; attempt++)
            {
                try
                {
                    var detailHtml = await _fetcher.FetchRenderedHtmlAsync(
                        teaser.Url,
                        "[data-js-event-occurrences]",
                        WaitForSelectorState.Attached,
                        ct);
                    var detail = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(detailHtml), ct);
                    rows.AddRange(BuildOccurrences(detail, teaser, today, horizonEnd, now));
                    break; // succeeded — on to the next teaser
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Skip this teaser if it never came good; the all-fail check
                    // below still surfaces a wholesale breakage.
                    lastFailure = ex;
                    if (attempt < TeaserFetchAttempts)
                        await Task.Delay(RetryDelay, ct);
                }
            }
        }

        if (rows.Count == 0 && attempted > 0 && lastFailure is not null)
            throw new InvalidOperationException(
                $"All {attempted} British Museum family event detail fetches failed; last error: {lastFailure.Message}",
                lastFailure);

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

    private IEnumerable<EventOccurrence> BuildOccurrences(IDocument detail, Teaser teaser, DateOnly from, DateOnly to, DateTimeOffset now)
    {
        var container = detail.QuerySelector("[data-js-event-occurrences]");
        if (container is null) yield break;

        var vid = container.GetAttribute("data-vid") ?? Slug(teaser.Url);
        var (minAge, maxAge) = TextParsing.ParseAgeRange(teaser.Summary);

        // The accordion is a flat sequence of: <h3 .accordion__heading><button><span>May 2026</span>…
        // followed by <div .accordion__content> containing <dl .occurrence-list>. Months can repeat
        // across an event so we iterate the accordion items in order.
        foreach (var item in container.QuerySelectorAll(".accordion__item"))
        {
            var monthLabel = item.QuerySelector(".accordion__button span")?.TextContent.Trim();
            if (!TryParseMonthYear(monthLabel, out var year, out var month)) continue;

            foreach (var occurrence in item.QuerySelectorAll(".occurrence-list__item"))
            {
                var dayLabel = occurrence.QuerySelector(".occurrence-list__days")?.TextContent.Trim();
                var day = ParseDayOfMonth(dayLabel);
                if (day is null) continue;

                DateOnly date;
                try { date = new DateOnly(year, month, day.Value); }
                catch (ArgumentOutOfRangeException) { continue; }
                if (date < from || date > to) continue;

                foreach (var timeEl in occurrence.QuerySelectorAll(".occurrence-list__time"))
                {
                    var (start, end) = ParseTimeRange(timeEl.TextContent);
                    if (start is null) continue;

                    yield return new EventOccurrence
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
                    };
                }
            }
        }
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
