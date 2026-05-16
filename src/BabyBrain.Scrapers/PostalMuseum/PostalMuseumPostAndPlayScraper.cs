using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using BabyBrain.Scrapers.Domain;
using BabyBrain.Scrapers.Shared;

namespace BabyBrain.Scrapers.PostalMuseum;

// Source: https://www.postalmuseum.org/event/post-and-play/
// A single recurring event on a WordPress page — not a listing. The intro
// block carries three ".single__row" rows, each keyed by an icon image:
//   date     → "Term-time Thursdays"
//   location → "Mail Rail building"
//   ticket   → "£5 Child ticket / Under 6 months go free" / times / "Under 5s"
//
// The site is behind Cloudflare, which fingerprints the TLS/HTTP2 stack and
// 403s a plain HttpClient even with browser-shaped headers — so we render
// through Playwright like the other CF-fronted sources.
//
// Recurrence is implicit weekly on the day named in the date row; we
// materialise one row per session per occurrence across the horizon — the
// same model as the Camden stay-and-play timetable. The ticket row holds
// two sessions ("10:15-11.00 and 11:15-12.00"), so each Thursday yields
// two rows.
public sealed class PostalMuseumPostAndPlayScraper : IScraper
{
    private const string EventUrl = "https://www.postalmuseum.org/event/post-and-play/";
    private const string Venue = "The Postal Museum";
    private const string Address = "15-20 Phoenix Place, London";
    private const string Postcode = "WC1X 0DA";

    public string SourceId => "postal_museum_post_and_play";
    public string Category => Categories.Museum;

    private readonly PlaywrightFetcher _fetcher;

    public PostalMuseumPostAndPlayScraper(PlaywrightFetcher fetcher) => _fetcher = fetcher;

    public async Task<IReadOnlyList<EventOccurrence>> ScrapeAsync(int horizonDays, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var horizonEnd = today.AddDays(horizonDays);
        var now = DateTimeOffset.UtcNow;

        var html = await _fetcher.FetchRenderedHtmlAsync(EventUrl, "div.single__row", ct: ct);
        var doc = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html), ct);

        var title = doc.QuerySelector("h1.title")?.TextContent?.Trim();
        if (string.IsNullOrEmpty(title))
            throw new InvalidOperationException("Postal Museum: event title (h1.title) not found");

        // The three intro rows are only distinguishable by their icon
        // image's alt text — there are no per-row classes.
        IElement? dateRow = null, ticketRow = null;
        foreach (var row in doc.QuerySelectorAll("div.single__row"))
        {
            var alt = row.QuerySelector("img")?.GetAttribute("alt")?.ToLowerInvariant() ?? "";
            if (alt.Contains("date")) dateRow = row;
            else if (alt.Contains("ticket")) ticketRow = row;
        }
        if (dateRow is null || ticketRow is null)
            throw new InvalidOperationException("Postal Museum: date/ticket rows not found");

        var dateText = dateRow.TextContent.Trim();
        var day = FindDayOfWeek(dateText)
            ?? throw new InvalidOperationException($"Postal Museum: no weekday in date row '{dateText}'");
        var termTimeOnly = dateText.Contains("term-time", StringComparison.OrdinalIgnoreCase)
                        || dateText.Contains("term time", StringComparison.OrdinalIgnoreCase);

        // The ticket row mixes price, session times and audience across
        // <br>-separated segments. Drop the booking button first so its
        // text doesn't leak into the segments we parse.
        ticketRow.QuerySelector(".book-btn")?.Remove();
        var segments = Regex.Split(ticketRow.InnerHtml, @"<br\s*/?>", RegexOptions.IgnoreCase)
            .Select(StripTags)
            .Where(s => s.Length > 0)
            .ToList();
        var ticketText = string.Join(" ", segments);

        var sessions = ParseSessionTimes(ticketText);
        if (sessions.Count == 0)
            throw new InvalidOperationException($"Postal Museum: no session times in '{ticketText}'");

        // Headline price. We don't run TextParsing.ParsePrice here because
        // "Under 6 months go free" would trip its free-detection — that's a
        // pricing exception, not the event being free.
        decimal? cost = null;
        var priceMatch = Regex.Match(ticketText, @"£\s*(\d+(?:\.\d{1,2})?)");
        if (priceMatch.Success && decimal.TryParse(priceMatch.Groups[1].Value,
                NumberStyles.Number, CultureInfo.InvariantCulture, out var v))
            cost = v;

        // Audience age sits in its own segment ("Under 5s"). Parsing the
        // whole ticket text would let the "Under 6 months" pricing clause
        // win — so prefer the segment with no price and no time.
        var ageSegment = segments.LastOrDefault(s =>
            !s.Contains('£') && !Regex.IsMatch(s, @"\d{1,2}[:.]\d{2}")) ?? ticketText;
        var (minAge, maxAge) = TextParsing.ParseAgeRange(ageSegment);

        var description = doc.QuerySelector("div.richtext:not(.intro) h3")?.TextContent?.Trim();
        var notes = string.IsNullOrEmpty(description) ? null : Truncate(description, 400);

        var rows = new List<EventOccurrence>();
        foreach (var date in TextParsing.WeeklyDatesInWindow(day, today, horizonEnd))
        {
            foreach (var (start, end) in sessions)
            {
                rows.Add(new EventOccurrence
                {
                    ExternalKey = $"{SourceId}:{date:yyyy-MM-dd}:{start:HHmm}",
                    Source = SourceId,
                    Category = Category,
                    SourceUrl = EventUrl,
                    Date = date,
                    StartTime = start,
                    EndTime = end,
                    SessionName = title,
                    SessionNotes = notes,
                    VenueName = Venue,
                    VenueAddress = Address,
                    Postcode = Postcode,
                    MinAgeMonths = minAge,
                    MaxAgeMonths = maxAge,
                    TermTimeOnly = termTimeOnly,
                    IsFree = false,
                    Cost = cost,
                    LastSeenAt = now,
                });
            }
        }
        return rows;
    }

    private static DayOfWeek? FindDayOfWeek(string text)
    {
        foreach (var word in Regex.Split(text, @"[^A-Za-z]+"))
        {
            var d = TextParsing.ParseDayOfWeek(word);
            if (d is not null) return d;
        }
        return null;
    }

    // Matches "10:15-11.00", "11:15 – 12:00" etc. Separators are mixed
    // (colon and dot) in the source, so accept either within a time.
    private static readonly Regex TimeRangeRegex = new(
        @"(\d{1,2})[:.](\d{2})\s*[-–]\s*(\d{1,2})[:.](\d{2})", RegexOptions.Compiled);

    private static List<(TimeOnly start, TimeOnly? end)> ParseSessionTimes(string text)
    {
        var list = new List<(TimeOnly, TimeOnly?)>();
        foreach (Match m in TimeRangeRegex.Matches(text))
        {
            var sh = int.Parse(m.Groups[1].Value);
            var sm = int.Parse(m.Groups[2].Value);
            var eh = int.Parse(m.Groups[3].Value);
            var em = int.Parse(m.Groups[4].Value);
            if (sh > 23 || sm > 59 || eh > 23 || em > 59) continue;
            list.Add((new TimeOnly(sh, sm), new TimeOnly(eh, em)));
        }
        return list;
    }

    private static string StripTags(string html) =>
        System.Net.WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ")).Trim();

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";
}
