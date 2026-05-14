using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using BabyBrain.Scrapers.Domain;
using BabyBrain.Scrapers.Shared;

namespace BabyBrain.Scrapers.Southbank;

// Source: https://www.southbankcentre.co.uk/visit-us/families/#upcoming-events
// Two-pass: the families hub lists curated family events in a carousel under
// #upcoming-events; each card's cover link goes to a detail page whose
// "Times & tickets" section embeds the canonical performance list as a
// <br>-separated paragraph ("Fri 15 May 2026, 10.30am, The Clore Ballroom").
public sealed class SouthbankCentreScraper : IScraper
{
    private const string HubUrl = "https://www.southbankcentre.co.uk/visit-us/families/";
    private const string Postcode = "SE1 8XX"; // Southbank Centre, Belvedere Road

    public string SourceId => "southbank_centre_families";
    public string Category => Categories.Concert;

    private readonly PlaywrightFetcher _fetcher;

    public SouthbankCentreScraper(PlaywrightFetcher fetcher) => _fetcher = fetcher;

    public async Task<IReadOnlyList<EventOccurrence>> ScrapeAsync(int horizonDays, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var horizonEnd = today.AddDays(horizonDays);
        var now = DateTimeOffset.UtcNow;
        var rows = new List<EventOccurrence>();

        var hubHtml = await _fetcher.FetchRenderedHtmlAsync(HubUrl, "#upcoming-events ~ * .c-event-card", ct: ct);
        var hub = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(hubHtml), ct);
        var teasers = ExtractTeasers(hub).ToList();

        foreach (var teaser in teasers)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var detailHtml = await _fetcher.FetchRenderedHtmlAsync(teaser.Url, "#times-tickets", ct: ct);
                var detail = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(detailHtml), ct);
                rows.AddRange(BuildOccurrences(detail, teaser, today, horizonEnd, now));
            }
            catch (Exception ex)
            {
                // Detail page without a recognisable times block (Tessitura embed events,
                // running installations with no fixed schedule, etc.) — skip but surface
                // the reason so we know which events we're missing.
                Console.WriteLine($"  skipped {teaser.Url}: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
            }
        }
        return rows;
    }

    private record Teaser(string Title, string Summary, string Url);

    private static IEnumerable<Teaser> ExtractTeasers(IDocument hub)
    {
        var anchor = hub.QuerySelector("#upcoming-events");
        if (anchor is null) yield break;
        // The carousel lives in a sibling .c-container__wrap after the anchor div.
        var section = anchor.Closest("section");
        if (section is null) yield break;

        foreach (var card in section.QuerySelectorAll(".c-event-card"))
        {
            var link = card.QuerySelector("a.c-event-card__cover-link");
            var href = link?.GetAttribute("href");
            if (string.IsNullOrEmpty(href)) continue;

            var title = card.QuerySelector(".c-event-card__title")?.TextContent.Trim() ?? "";
            var summary = card.QuerySelector(".c-event-card__listing-details")?.TextContent.Trim() ?? "";
            yield return new Teaser(title, summary, href);
        }
    }

    private IEnumerable<EventOccurrence> BuildOccurrences(IDocument detail, Teaser teaser, DateOnly from, DateOnly to, DateTimeOffset now)
    {
        var (minAge, maxAge) = TextParsing.ParseAgeRange(teaser.Title + " " + teaser.Summary);
        var isFree = DetectFree(detail);
        var notes = ExtractDescription(detail) ?? (teaser.Summary.Length > 0 ? teaser.Summary : null);

        // Canonical performance list is the .smaller-p paragraph inside the
        // override block. Each <br>-separated line is one performance.
        var overrideBlock = detail.QuerySelector(".c-event-details-group__date-time-override-information .smaller-p");
        if (overrideBlock is null) yield break;

        var html = overrideBlock.InnerHtml;
        var rawLines = Regex.Split(html, @"<br\s*/?>", RegexOptions.IgnoreCase);

        foreach (var raw in rawLines)
        {
            var text = Regex.Replace(raw, "<[^>]+>", "").Trim();
            if (text.Length == 0) continue;

            var parsed = ParseLine(text);
            if (parsed is null) continue;
            if (parsed.Date < from || parsed.Date > to) continue;

            yield return new EventOccurrence
            {
                ExternalKey = $"{SourceId}:{Slug(teaser.Url)}:{parsed.Date:yyyy-MM-dd}:{parsed.StartTime:HHmm}",
                Source = SourceId,
                Category = Category,
                SourceUrl = teaser.Url,
                Date = parsed.Date,
                StartTime = parsed.StartTime,
                EndTime = null,
                SessionName = teaser.Title,
                SessionNotes = notes,
                VenueName = parsed.Venue,
                VenueAddress = "Belvedere Road, London",
                Postcode = Postcode,
                MinAgeMonths = minAge,
                MaxAgeMonths = maxAge,
                TermTimeOnly = false,
                IsFree = isFree,
                LastSeenAt = now,
            };
        }
    }

    // Southbank renders free events as a disabled "Free – no ticket required"
    // pill inside the event masthead's booking block, identified by the class
    // c-btn--free-no-ticket. Fall back to "Free" text matching in case the
    // class name shifts but the wording stays.
    private static bool DetectFree(IDocument detail)
    {
        var booking = detail.QuerySelector(".c-event-masthead__event-booking");
        if (booking is null) return false;
        if (booking.QuerySelector(".c-btn--free-no-ticket") is not null) return true;
        var (isFree, _) = TextParsing.ParsePrice(booking.TextContent);
        return isFree;
    }

    // The actual marketing copy lives in .c-event-masthead__intro on the detail
    // page (a single <p> usually). Earlier versions used .c-event-card__listing-details
    // from the hub-page teaser, which is listing-metadata badges rather than prose.
    // Returns null if the block isn't present so the caller can fall back.
    private static string? ExtractDescription(IDocument detail)
    {
        var intro = detail.QuerySelector(".c-event-masthead__intro")?.TextContent?.Trim();
        if (string.IsNullOrEmpty(intro)) return null;
        var collapsed = Regex.Replace(intro, @"\s+", " ").Trim();
        return Truncate(collapsed, 400);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";

    private record ParsedLine(DateOnly Date, TimeOnly StartTime, string Venue);

    private static readonly Regex LineRegex = new(
        @"^(?<wday>\w+)\s+(?<d>\d{1,2})\s+(?<m>\w+)\s+(?<y>\d{4}),\s+(?<t>\d{1,2}(?:[\.:]\d{2})?\s*(?:am|pm)),\s+(?<venue>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static ParsedLine? ParseLine(string line)
    {
        var m = LineRegex.Match(line);
        if (!m.Success) return null;

        if (!int.TryParse(m.Groups["d"].Value, out var day)) return null;
        if (!int.TryParse(m.Groups["y"].Value, out var year)) return null;
        if (!TryParseMonth(m.Groups["m"].Value, out var month)) return null;

        DateOnly date;
        try { date = new DateOnly(year, month, day); }
        catch (ArgumentOutOfRangeException) { return null; }

        var time = TextParsing.ParseClockTime(m.Groups["t"].Value);
        if (time is null) return null;

        var venue = m.Groups["venue"].Value.Trim();
        // The detail page sometimes appends a hall/foyer ("The Clore Ballroom Level 2,
        // Royal Festival Hall") — keep everything as the venue label.
        return new ParsedLine(date, time.Value, venue);
    }

    private static bool TryParseMonth(string raw, out int month)
    {
        month = 0;
        if (DateTime.TryParseExact(raw, "MMMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            || DateTime.TryParseExact(raw, "MMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
        {
            month = dt.Month;
            return true;
        }
        return false;
    }

    private static string Slug(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
}
