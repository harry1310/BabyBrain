using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using BabyBrain.Scrapers.Domain;
using BabyBrain.Scrapers.Shared;
using Microsoft.Extensions.Logging;

namespace BabyBrain.Scrapers.Islington;

// Scrapes Islington's "Under 5s activities" listing from the Find Your
// Islington directory (Open Objects KB5 platform). Covers libraries,
// children's centres, stay-and-play groups, and music sessions — whatever
// the directory lists under activity=4 with a parseable session schedule.
// Two passes:
//   1. Fetch the listing page (activity=4 = under-5s) and extract every
//      service.page?id=… link.
//   2. For each service, fetch the detail page and parse the "Session
//      Information" field. Lines look like "Baby Bounce Fridays 11am" — some
//      services split across two lines ("Baby Bounce:\nFridays, 11am").
// Sessions are weekly recurring; we materialise occurrences across the
// horizon. Category is inferred per row from the service name (a "Library"
// service emits Library rows; everything else emits Community).
public sealed class IslingtonFindYourScraper : IScraper
{
    // `sr` is "start record" (offset, not page size). The directory paginates
    // in groups of 50; we walk pages until one returns no service links.
    private const string ListingUrlTemplate =
        "https://findyour.islington.gov.uk/kb5/islington/directory/results.action?activity=4&sorttype=field&sortfield=title&adv_sr=0&familychannelnew=0&sr={0}";
    private const int PageSize = 50;
    private const int MaxPages = 10;

    public string SourceId => "islington_findyour";
    // Default category — most under-5s services in the directory are libraries
    // by count, but each row's actual category is set per-service in ParsePage.
    public string Category => Categories.Library;

    private readonly PlaywrightFetcher _fetcher;
    private readonly ILogger<IslingtonFindYourScraper> _logger;

    public IslingtonFindYourScraper(PlaywrightFetcher fetcher, ILogger<IslingtonFindYourScraper> logger)
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

        var serviceIds = await DiscoverServiceIdsAsync(ct);
        _logger.LogInformation("Islington discovery found {Count} under-5s services", serviceIds.Count);

        foreach (var id in serviceIds)
        {
            try
            {
                var url = $"https://findyour.islington.gov.uk/kb5/islington/directory/service.page?id={id}";
                // Wait on <main> rather than section.service_venue: not every
                // service page has the same shape (some are advice pages, some
                // children's centre blurbs with no sessions field). Parsing
                // simply yields zero rows for non-matching pages — much faster
                // than 30s timeouts.
                var html = await _fetcher.FetchRenderedHtmlAsync(url, "main", ct: ct);
                var doc = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html), ct);
                rows.AddRange(ParsePage(doc, url, today, horizonEnd, now));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Islington service page failed: {Id}", id);
            }
        }
        return rows;
    }

    private async Task<List<string>> DiscoverServiceIdsAsync(CancellationToken ct)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 0; page < MaxPages; page++)
        {
            var url = string.Format(ListingUrlTemplate, page * PageSize);
            int before = ids.Count;
            try
            {
                var html = await _fetcher.FetchRenderedHtmlAsync(url, "ol.results-list", ct: ct);
                var doc = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html), ct);
                foreach (var a in doc.QuerySelectorAll("ol.results-list a[href*='service.page']"))
                {
                    var href = a.GetAttribute("href") ?? "";
                    var m = Regex.Match(href, @"id=([^&]+)");
                    if (m.Success) ids.Add(Uri.UnescapeDataString(m.Groups[1].Value));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Islington listing page failed: sr={Sr}", page * PageSize);
                break;
            }
            // Stop when a page yields no new ids (we've walked past the last results page).
            if (ids.Count == before) break;
        }
        return ids.ToList();
    }

    private IEnumerable<EventOccurrence> ParsePage(IDocument doc, string url, DateOnly from, DateOnly to, DateTimeOffset now)
    {
        var serviceName = doc.QuerySelector("h1")?.TextContent.Trim() ?? "Under 5s service";
        var category = InferCategory(serviceName);
        var (venue, address, postcode) = ExtractLocation(doc);
        foreach (var line in ExtractSessionLines(doc))
        {
            var parsed = ParseSessionLine(line);
            if (parsed is null) continue;
            foreach (var date in TextParsing.WeeklyDatesInWindow(parsed.Day, from, to))
            {
                yield return new EventOccurrence
                {
                    ExternalKey = $"{SourceId}:{Slug(venue)}:{Slug(parsed.Name)}:{date:yyyy-MM-dd}:{parsed.StartTime:HHmm}",
                    Source = SourceId,
                    Category = category,
                    SourceUrl = url,
                    Date = date,
                    StartTime = parsed.StartTime,
                    EndTime = null,
                    SessionName = parsed.Name,
                    SessionNotes = serviceName,
                    VenueName = venue,
                    VenueAddress = address,
                    Postcode = postcode,
                    MinAgeMonths = parsed.MinAge,
                    MaxAgeMonths = parsed.MaxAge,
                    TermTimeOnly = false,
                    IsFree = true,
                    LastSeenAt = now,
                };
            }
        }
    }

    private static (string venue, string? address, string? postcode) ExtractLocation(IDocument doc)
    {
        var section = doc.QuerySelector("section.service_venue");
        if (section is null) return ("", null, null);
        string venue = "", postcode = "";
        var addrParts = new List<string>();
        foreach (var dt in section.QuerySelectorAll("dt"))
        {
            var dd = NextDd(dt);
            if (dd is null) continue;
            switch (dt.TextContent.Trim())
            {
                case "Name of venue": venue = dd.TextContent.Trim(); break;
                case "Address":
                    addrParts = dd.QuerySelectorAll("span").Select(s => s.TextContent.Trim()).Where(s => s.Length > 0).ToList();
                    break;
                case "Postcode": postcode = dd.TextContent.Trim(); break;
            }
        }
        return (venue, addrParts.Count > 0 ? string.Join(", ", addrParts) : null, string.IsNullOrEmpty(postcode) ? null : postcode);
    }

    private static IEnumerable<string> ExtractSessionLines(IDocument doc)
    {
        var raw = new List<string>();
        foreach (var dt in doc.QuerySelectorAll("dt"))
        {
            if (dt.TextContent.Trim() != "Session Information") continue;
            var dd = NextDd(dt);
            if (dd is null) break;
            foreach (var piece in Regex.Split(dd.InnerHtml, @"<br\s*/?>", RegexOptions.IgnoreCase))
            {
                var text = Regex.Replace(piece, "<[^>]+>", "").Trim();
                if (text.Length > 0) raw.Add(text);
            }
            break;
        }

        // Merge "Name:" + "Day, time" pairs (some libraries split across two lines).
        var dayStart = new Regex(@"^\s*(?:Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday)s?\b", RegexOptions.IgnoreCase);
        for (var i = 0; i < raw.Count; i++)
        {
            var line = raw[i];
            if (line.EndsWith(':') && i + 1 < raw.Count && dayStart.IsMatch(raw[i + 1]))
            {
                yield return $"{line.TrimEnd(':').Trim()} {raw[i + 1].Trim()}";
                i++;
            }
            else
            {
                yield return line;
            }
        }
    }

    private record ParsedSession(string Name, DayOfWeek Day, TimeOnly StartTime, int? MinAge, int? MaxAge);

    private static readonly Regex SessionRegex = new(
        @"^(?<name>.+?)\s+(?<day>Mondays?|Tuesdays?|Wednesdays?|Thursdays?|Fridays?|Saturdays?|Sundays?)\s*,?\s*(?<time>\d{1,2}(?:[\.:]\d{2})?\s*(?:am|pm))\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static ParsedSession? ParseSessionLine(string line)
    {
        var m = SessionRegex.Match(line);
        if (!m.Success) return null;
        var day = TextParsing.ParseDayOfWeek(m.Groups["day"].Value);
        var time = TextParsing.ParseClockTime(m.Groups["time"].Value);
        if (day is null || time is null) return null;
        // Strip trailing punctuation/separators ("Baby Bounce -" → "Baby Bounce").
        var name = m.Groups["name"].Value.Trim().TrimEnd(' ', '-', '–', ':', ',');
        var (min, max) = AgeFromSessionName(name);
        return new ParsedSession(name, day.Value, time.Value, min, max);
    }

    private static (int? min, int? max) AgeFromSessionName(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("baby bounce") || n.Contains("baby rhyme")) return (0, 18);
        if (n.Contains("under 5") || n.Contains("under-5") || n.Contains("under five")) return (0, 60);
        if (n.Contains("under 1") || n.Contains("baby play")) return (0, 12);
        if (n.Contains("toddler")) return (12, 36);
        if (n.Contains("story time") || n.Contains("storytime")) return (12, 60);
        return (null, null);
    }

    private static IElement? NextDd(IElement dt)
    {
        var sib = dt.NextElementSibling;
        while (sib is { NodeName: not "DD" }) sib = sib.NextElementSibling;
        return sib;
    }

    private static string Slug(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');

    // Categorise a row by the service's own name. The "Find Your Islington"
    // directory's under-5s listing is a mix of libraries, children's centres,
    // and other community spaces — they shouldn't all share one category.
    private static string InferCategory(string serviceName)
    {
        var n = serviceName.ToLowerInvariant();
        if (n.Contains("library")) return Categories.Library;
        return Categories.Community;
    }
}
