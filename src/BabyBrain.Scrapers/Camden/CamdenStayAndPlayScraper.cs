using System.Text.RegularExpressions;
using AngleSharp;
using BabyBrain.Scrapers.Domain;
using BabyBrain.Scrapers.Shared;

namespace BabyBrain.Scrapers.Camden;

// Source: https://families.camden.gov.uk/full-stay-play-timetable/
// Format: <h2>Day</h2> ... <h3>Session name (- age suffix)</h3> followed by one
// or more <p> lines, each shaped "start to end [(notes)], venue, address, postcode".
// Recurrence is implicit weekly; we materialise occurrences across the horizon.
public sealed class CamdenStayAndPlayScraper : IScraper
{
    private const string Url = "https://families.camden.gov.uk/full-stay-play-timetable/";
    public string SourceId => "camden_stay_and_play";
    public string Category => Categories.Community;

    private readonly PlaywrightFetcher _fetcher;

    public CamdenStayAndPlayScraper(PlaywrightFetcher fetcher) => _fetcher = fetcher;

    public async Task<IReadOnlyList<EventOccurrence>> ScrapeAsync(int horizonDays, CancellationToken ct = default)
    {
        var html = await _fetcher.FetchRenderedHtmlAsync(Url, ".lbcamden-prose h2", ct: ct);
        var doc = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html), ct);
        var prose = doc.QuerySelector(".lbcamden-prose")
            ?? throw new InvalidOperationException("Camden: .lbcamden-prose not found");

        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var horizonEnd = today.AddDays(horizonDays);
        var now = DateTimeOffset.UtcNow;
        var rows = new List<EventOccurrence>();

        DayOfWeek? currentDay = null;
        string? currentSession = null;
        string? currentNotes = null;
        (int? min, int? max) currentAge = (null, null);

        foreach (var node in prose.Children)
        {
            switch (node.NodeName)
            {
                case "H2":
                    currentDay = TextParsing.ParseDayOfWeek(node.TextContent);
                    currentSession = null;
                    break;
                case "H3":
                    var (name, notes, age) = ParseSessionHeading(node.TextContent.Trim());
                    currentSession = name;
                    currentNotes = notes;
                    currentAge = age;
                    break;
                case "P":
                    if (currentDay is null || currentSession is null) break;
                    var line = ParseSessionLine(node.TextContent);
                    if (line is null) break;
                    foreach (var date in TextParsing.WeeklyDatesInWindow(currentDay.Value, today, horizonEnd))
                    {
                        rows.Add(new EventOccurrence
                        {
                            ExternalKey = $"{SourceId}:{Slug(line.VenueName)}:{Slug(currentSession)}:{date:yyyy-MM-dd}:{line.StartTime:HHmm}",
                            Source = SourceId,
                            Category = Category,
                            SourceUrl = Url,
                            Date = date,
                            StartTime = line.StartTime,
                            EndTime = line.EndTime,
                            SessionName = currentSession,
                            SessionNotes = currentNotes,
                            VenueName = line.VenueName,
                            VenueAddress = line.VenueAddress,
                            Postcode = line.Postcode,
                            MinAgeMonths = currentAge.min,
                            MaxAgeMonths = currentAge.max,
                            TermTimeOnly = line.TermTimeOnly,
                            LastSeenAt = now,
                        });
                    }
                    break;
            }
        }
        return rows;
    }

    private static (string name, string? notes, (int? min, int? max) age) ParseSessionHeading(string heading)
    {
        var h = heading.Replace(" ", " ").Trim();
        string? notes = null;
        var paren = Regex.Match(h, @"\s*\(([^)]+)\)\s*$");
        if (paren.Success)
        {
            notes = paren.Groups[1].Value.Trim();
            h = h[..paren.Index].Trim();
        }
        var split = Regex.Match(h, @"^(?<name>.+?)\s+[\-–]\s+(?<suffix>.+)$");
        var name = split.Success ? split.Groups["name"].Value.Trim() : h;
        var suffix = split.Success ? split.Groups["suffix"].Value.Trim() : "";
        return (name, notes, TextParsing.ParseAgeRange(suffix.Length > 0 ? suffix : name));
    }

    private record ParsedLine(TimeOnly StartTime, TimeOnly? EndTime, string VenueName, string VenueAddress, string? Postcode, bool TermTimeOnly);

    private static readonly Regex LineRegex = new(
        @"^(?<start>\d{1,2}(?:\.\d{2})?\s*(?:am|pm)|midday|noon|midnight)\s+to\s+(?<end>\d{1,2}(?:\.\d{2})?\s*(?:am|pm)|midday|noon|midnight)(?:\s*\((?<note>[^)]+)\))?\s*,\s*(?<rest>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static ParsedLine? ParseSessionLine(string raw)
    {
        var t = raw.Replace(" ", " ").Replace("’", "'").Trim();
        var m = LineRegex.Match(t);
        if (!m.Success) return null;
        var start = TextParsing.ParseClockTime(m.Groups["start"].Value);
        var end = TextParsing.ParseClockTime(m.Groups["end"].Value);
        if (start is null) return null;
        var note = m.Groups["note"].Success ? m.Groups["note"].Value.Trim() : null;
        var rest = m.Groups["rest"].Value.Trim();
        var commaIdx = rest.IndexOf(',');
        if (commaIdx < 0) return null;
        var venueName = rest[..commaIdx].Trim();
        var address = rest[(commaIdx + 1)..].Trim();
        var postcode = Regex.Match(address, @"\b([A-Z]{1,2}\d{1,2}[A-Z]?\s*\d[A-Z]{2})\b").Value;
        var termTimeOnly = note?.Contains("term-time", StringComparison.OrdinalIgnoreCase) == true;
        return new ParsedLine(start.Value, end, venueName, address, string.IsNullOrEmpty(postcode) ? null : postcode, termTimeOnly);
    }

    private static string Slug(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
}
