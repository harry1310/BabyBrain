using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using BabyBrain.Scrapers.Domain;
using BabyBrain.Scrapers.Shared;

namespace BabyBrain.Scrapers.MomeLondon;

// Source: https://momelondon.com/creative-classes/
// Mome London's creative classes for babies/toddlers (painting, music, dance,
// story, messy play) at their Angel play space. Recurring weekly sessions, so
// we materialise concrete dated occurrences across the horizon.
//
// Server-rendered WordPress/Elementor HTML, no JS challenge, so a plain
// browser-UA HttpClient is enough — no Playwright.
//
// IMPORTANT: the page carries TWO conflicting schedules. The visible content is
// a set of "class cards" (one start time + a day or two per class); a much more
// detailed weekly timetable grid also exists in the markup but every one of its
// containers is `elementor-hidden-desktop/tablet/mobile`, i.e. hidden from every
// visitor — likely a draft. We deliberately ingest ONLY the visible cards so the
// directory matches what families actually see on momelondon.com. If Mome ever
// publishes the detailed grid (drops the hidden- classes), revisit this.
//
// Each card:
//   <div class="clases-content">
//     <h4><a href="…/all-project/painting/">Painting</a></h4>
//     <p>Creative mark-making…</p>
//     <ul class="clases-schedule">
//       <li><span>Age</span> <br>(6months-4years)</li>
//       <li><span>Day</span> <br>Monday &amp; Saturday</li>
//       <li><span>time</span> <br>10.00</li>
//     </ul>
//   </div>
public sealed class MomeLondonCreativeClassesScraper : IScraper
{
    private const string Url = "https://momelondon.com/creative-classes/";

    public string SourceId => "mome_london_creative_classes";
    public string Category => Categories.Class;

    // One fixed venue — the address isn't repeated per card, so stamp it for
    // geocoding/display.
    private const string VenueName = "Mome London";
    private const string VenueAddress = "408-410 St John Street, Angel, London";
    private const string Postcode = "EC1V 4NJ";

    // The site states each class runs 45 minutes (plus complimentary soft play);
    // cards publish only a start time, so we derive the end from the duration.
    private static readonly TimeSpan SessionLength = TimeSpan.FromMinutes(45);

    private readonly HttpClient _http;

    public MomeLondonCreativeClassesScraper(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<EventOccurrence>> ScrapeAsync(int horizonDays, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var horizonEnd = today.AddDays(horizonDays);
        var now = DateTimeOffset.UtcNow;
        var rows = new List<EventOccurrence>();

        var html = await _http.GetStringAsync(Url, ct);
        var doc = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html), ct);

        var cards = doc.QuerySelectorAll("ul.clases-schedule");
        if (cards.Length == 0)
            throw new InvalidOperationException("Mome London: no clases-schedule cards found");

        foreach (var schedule in cards)
        {
            ct.ThrowIfCancellationRequested();

            // The card's title/description/link live alongside the <ul> under the
            // shared .clases-content wrapper.
            var content = schedule.Closest(".clases-content") ?? schedule.ParentElement;
            var titleLink = content?.QuerySelector("h4 a");
            var title = Clean(titleLink?.TextContent ?? "");
            if (title.Length == 0) continue;

            var sourceUrl = titleLink?.GetAttribute("href") is { Length: > 0 } href ? href : Url;
            var notes = Clean(content?.QuerySelector("p")?.TextContent ?? "").TrimEnd('…', '.', ' ');

            // The three <li> are labelled by a leading <span> (Age / Day / time);
            // the value is the text after it. Map by label, case-insensitively.
            var fields = ReadFields(schedule);
            if (!fields.TryGetValue("day", out var dayText) || !fields.TryGetValue("time", out var timeText))
                continue;

            var start = TextParsing.ParseClockTime(timeText);
            if (start is null) continue;
            var end = start.Value.Add(SessionLength);

            var (minAge, maxAge) = fields.TryGetValue("age", out var ageText) ? ParseAge(ageText) : (null, null);

            foreach (var day in ParseDays(dayText))
            {
                foreach (var date in TextParsing.WeeklyDatesInWindow(day, today, horizonEnd))
                {
                    rows.Add(new EventOccurrence
                    {
                        ExternalKey = $"{SourceId}:{Slug(title)}:{date:yyyy-MM-dd}:{start:HHmm}",
                        Source = SourceId,
                        Category = Category,
                        SourceUrl = sourceUrl,
                        Date = date,
                        StartTime = start.Value,
                        EndTime = end,
                        TimeApproximate = false,
                        SessionName = title,
                        SessionNotes = notes.Length > 0 ? notes : null,
                        VenueName = VenueName,
                        VenueAddress = VenueAddress,
                        Postcode = Postcode,
                        MinAgeMonths = minAge,
                        MaxAgeMonths = maxAge,
                        TermTimeOnly = false,
                        // Pricing isn't on the page (membership-based); leave unknown
                        // rather than claim free.
                        IsFree = false,
                        LastSeenAt = now,
                    });
                }
            }
        }

        return rows;
    }

    // li label (lower-cased, from the <span>) -> value (text after the <span>).
    private static Dictionary<string, string> ReadFields(IElement schedule)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var li in schedule.QuerySelectorAll("li"))
        {
            var label = Clean(li.QuerySelector("span")?.TextContent ?? "").ToLowerInvariant();
            if (label.Length == 0) continue;
            // Value = the li's text minus the label span's text.
            var value = Clean(li.TextContent);
            if (value.StartsWith(label, StringComparison.OrdinalIgnoreCase))
                value = value[label.Length..].Trim();
            if (value.Length > 0 && !fields.ContainsKey(label)) fields[label] = value;
        }
        return fields;
    }

    // "Monday & Saturday" / "Friday" / "Wednesday & Sunday" -> day(s).
    private static IEnumerable<DayOfWeek> ParseDays(string text)
    {
        var seen = new HashSet<DayOfWeek>();
        foreach (var part in Regex.Split(text, @"\s*(?:&|and|,|/)\s*"))
        {
            if (TextParsing.ParseDayOfWeek(part.Trim()) is { } d && seen.Add(d))
                yield return d;
        }
    }

    // "(6months-4years)" / "(3months-4years)" -> age range in months. Each side
    // is "<n>months" or "<n>years"; years are converted to months.
    private static (int? min, int? max) ParseAge(string raw)
    {
        var parts = Regex.Split(raw.Trim('(', ')', ' '), @"\s*[-–—]\s*");
        if (parts.Length != 2) return (null, null);
        var min = AgeToMonths(parts[0]);
        var max = AgeToMonths(parts[1]);
        return (min, max);
    }

    private static int? AgeToMonths(string token)
    {
        var m = Regex.Match(token, @"(\d+)\s*(month|year)", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return m.Groups[2].Value.StartsWith("year", StringComparison.OrdinalIgnoreCase) ? n * 12 : n;
    }

    // Collapse whitespace, including the &nbsp; the CMS sprinkles in.
    private static string Clean(string s) => Regex.Replace(s.Replace(' ', ' '), @"\s+", " ").Trim();

    private static string Slug(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
}
