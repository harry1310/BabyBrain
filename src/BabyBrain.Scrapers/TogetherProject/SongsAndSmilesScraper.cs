using System.Net;
using System.Text.RegularExpressions;
using BabyBrain.Scrapers.Domain;
using BabyBrain.Scrapers.Shared;

namespace BabyBrain.Scrapers.TogetherProject;

// Source: https://thetogetherproject.org.uk/songs-smiles-booking
// The Together Project's "Songs & Smiles" — free intergenerational singing
// sessions for under-4s and care-home residents. The booking page lists every
// venue nationally, grouped under <h4> region headings ("London (North)",
// "London (North-West)", "Essex", "Greater Manchester", …). We currently take
// only the North and North-West London sections (see IncludedRegionToken) and
// ignore the rest.
//
// The page is a Squarespace build with deeply-nested, hashed block markup, so
// rather than rely on CSS structure we slice the HTML between region <h4>
// headings and read each venue from its predictable shape: a maps link whose
// text is "<VenueName><br><Address incl. postcode>", immediately followed by a
// "<Weekday>s at <time>" line. Each venue recurs weekly on that day; we
// materialise one row per occurrence across the horizon.
//
// Age (0-4) and the free price aren't on the booking page itself — they're
// stated on the programme page (/programmes/songs-and-smiles: "Designed for
// children aged 0-4" / "Songs & Smiles is free to attend!") — so they're fixed
// here rather than scraped.
public sealed class SongsAndSmilesScraper : IScraper
{
    private const string BookingUrl = "https://thetogetherproject.org.uk/songs-smiles-booking";
    private const string SessionTitle = "Songs & Smiles";

    // Region <h4> headings to keep. "north" matches both "London (North)" and
    // "London (North-West)" and nothing else on the page; widen this to take
    // more regions later.
    private const string IncludedRegionToken = "north";

    public string SourceId => "together_project_songs_smiles";
    public string Category => Categories.Community;

    private readonly HttpClient _http;

    public SongsAndSmilesScraper(HttpClient http) => _http = http;

    private static readonly Regex H4Heading = new(@"<h4\b[^>]*>(?<inner>.*?)</h4>",
        RegexOptions.Singleline | RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Each venue is a <p> whose text is "Name<br>Address". The wrappers vary
    // (name/address may be one anchor, two anchors, or span-wrapped), so we
    // match the paragraph and split on the <br> rather than assume a structure.
    private static readonly Regex VenueParagraph = new(
        @"<p\b[^>]*>(?<inner>.*?)</p>", RegexOptions.Singleline | RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BrSplit = new(@"<br\s*/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Postcode = new(
        @"\b([A-Z]{1,2}\d{1,2}[A-Z]?\s*\d[A-Z]{2})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DayTime = new(
        @"(?<day>Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday)s?\s+(?:at\s+)?(?<h>\d{1,2})[.:](?<m>\d{2})\s*(?<ap>[AP]M)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<IReadOnlyList<EventOccurrence>> ScrapeAsync(int horizonDays, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var horizonEnd = today.AddDays(horizonDays);
        var now = DateTimeOffset.UtcNow;

        var html = await _http.GetStringAsync(BookingUrl, ct);

        var sections = RegionSections(html);
        if (sections.Count == 0)
            throw new InvalidOperationException(
                $"Songs & Smiles: no region <h4> headings containing '{IncludedRegionToken}' found — page structure may have changed.");

        var rows = new List<EventOccurrence>();
        foreach (var section in sections)
        {
            foreach (var venue in ParseVenues(section))
            {
                foreach (var date in TextParsing.WeeklyDatesInWindow(venue.Day, today, horizonEnd))
                {
                    rows.Add(new EventOccurrence
                    {
                        ExternalKey = $"{SourceId}:{Slug(venue.Name)}:{date:yyyy-MM-dd}:{venue.Start:HHmm}",
                        Source = SourceId,
                        Category = Category,
                        SourceUrl = BookingUrl,
                        Date = date,
                        StartTime = venue.Start,
                        EndTime = null,
                        SessionName = SessionTitle,
                        SessionNotes = BuildNotes(venue.Name),
                        VenueName = venue.Name,
                        VenueAddress = venue.Address,
                        Postcode = venue.Postcode,
                        MinAgeMonths = 0,
                        MaxAgeMonths = 48, // "Designed for children aged 0-4"
                        TermTimeOnly = false,
                        IsFree = true,
                        LastSeenAt = now,
                    });
                }
            }
        }

        // Spring Grove (Finchley Road) is listed under both North and North-West;
        // ExternalKey is unique, so collapse the duplicate.
        return rows
            .GroupBy(r => r.ExternalKey)
            .Select(g => g.First())
            .ToList();
    }

    // The HTML slice between each included region <h4> and the next <h4> of any
    // kind (which bounds the section).
    private static List<string> RegionSections(string html)
    {
        var sections = new List<string>();
        foreach (Match h in H4Heading.Matches(html))
        {
            var text = Collapse(StripTags(h.Groups["inner"].Value));
            if (!text.Contains("London", StringComparison.OrdinalIgnoreCase)) continue;
            if (!text.Contains(IncludedRegionToken, StringComparison.OrdinalIgnoreCase)) continue;

            var start = h.Index + h.Length;
            var nextH4 = html.IndexOf("<h4", start, StringComparison.OrdinalIgnoreCase);
            var end = nextH4 < 0 ? html.Length : nextH4;
            sections.Add(html[start..end]);
        }
        return sections;
    }

    private readonly record struct Venue(string Name, string Address, string? Postcode, DayOfWeek Day, TimeOnly Start);

    private static IEnumerable<Venue> ParseVenues(string section)
    {
        foreach (Match p in VenueParagraph.Matches(section))
        {
            var inner = p.Groups["inner"].Value;
            var parts = BrSplit.Split(inner);
            if (parts.Length < 2) continue; // not a "Name<br>Address" block

            var name = Decode(StripTags(parts[0]));
            var addr = Decode(StripTags(string.Join(" ", parts[1..])));
            var pc = Postcode.Match(addr);
            if (!pc.Success || name.Length == 0) continue; // not a venue block

            // The session day/time sits in the next <p>, right after this one.
            var dt = DayTime.Match(section, p.Index + p.Length);
            if (!dt.Success) continue;
            if (TextParsing.ParseDayOfWeek(dt.Groups["day"].Value) is not DayOfWeek day) continue;
            if (ToTime(dt) is not TimeOnly start) continue;

            yield return new Venue(name, addr, NormalisePostcode(pc.Value), day, start);
        }
    }

    private static TimeOnly? ToTime(Match dt)
    {
        var hour = int.Parse(dt.Groups["h"].Value);
        var minute = int.Parse(dt.Groups["m"].Value);
        var pm = dt.Groups["ap"].Value.Equals("PM", StringComparison.OrdinalIgnoreCase);
        if (pm && hour != 12) hour += 12;
        if (!pm && hour == 12) hour = 0;
        if (hour > 23 || minute > 59) return null;
        return new TimeOnly(hour, minute);
    }

    private static string BuildNotes(string venueName) =>
        $"Free intergenerational singing session for under-4s and the residents of {venueName} — " +
        "part playdate, part singsong. Book via The Together Project.";

    private static string NormalisePostcode(string raw)
    {
        var compact = Regex.Replace(raw.ToUpperInvariant(), @"\s+", "");
        return compact.Length >= 5
            ? $"{compact[..^3]} {compact[^3..]}" // "N103PA" → "N10 3PA"
            : raw.Trim();
    }

    private static string Decode(string s) => Collapse(WebUtility.HtmlDecode(s));
    private static string StripTags(string html) => Regex.Replace(html, "<[^>]+>", " ");
    private static string Collapse(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    private static string Slug(string s) =>
        Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
}
