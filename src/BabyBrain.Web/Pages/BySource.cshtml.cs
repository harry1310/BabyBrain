using BabyBrain.Scrapers.Domain;
using BabyBrain.Web.Data;
using BabyBrain.Web.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BabyBrain.Web.Pages;

// "Browse by source": pick one source from a dropdown, get its upcoming events
// in chronological order. A plainer companion to the Index search page.
public class BySourceModel : PageModel
{
    // "Upcoming" is relative to today in London, not the UTC server clock —
    // same reasoning as IndexModel (production container runs UTC).
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    // A single source over the 90-day scrape horizon is at most a few dozen
    // rows; this cap is just a safety belt against an unexpectedly huge source.
    private const int MaxResults = 400;

    private readonly BabyBrainDbContext _db;
    private readonly UkBankHolidayService _holidays;
    public BySourceModel(BabyBrainDbContext db, UkBankHolidayService holidays)
    {
        _db = db;
        _holidays = holidays;
    }

    public IReadOnlyList<SourceOption> SourceOptions { get; private set; } = Array.Empty<SourceOption>();
    public string? SelectedSource { get; private set; }
    public List<EventOccurrence> Results { get; private set; } = new();

    public async Task OnGetAsync(string? source)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, London).Date);

        // Build the dropdown from sources that actually have upcoming events.
        // Sources.Label turns the SourceId token into a recognisable name.
        var upcoming = await _db.EventOccurrences
            .Where(e => e.Date >= today)
            .GroupBy(e => e.Source)
            .Select(g => new { Source = g.Key, Count = g.Count() })
            .ToListAsync();

        SourceOptions = upcoming
            .Select(g => new SourceOption(g.Source, Sources.Label(g.Source), g.Count))
            .OrderBy(s => s.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Only honour a source that's actually in the dropdown — an unknown or
        // stale ?source= value falls through to the "pick one" prompt.
        if (!string.IsNullOrEmpty(source) && SourceOptions.Any(s => s.Id == source))
        {
            SelectedSource = source;
            var q = _db.EventOccurrences
                .Where(e => e.Source == source && e.Date >= today);

            // Hide community + library events on UK bank holidays — matches the
            // Index search filter. See IndexModel for the rationale.
            var upcomingHolidays = _holidays.Holidays.Where(d => d >= today).ToList();
            if (upcomingHolidays.Count > 0)
            {
                q = q.Where(e => !upcomingHolidays.Contains(e.Date)
                    || (e.Category != Categories.Community && e.Category != Categories.Library));
            }

            Results = await q
                .OrderBy(e => e.Date).ThenBy(e => e.StartTime)
                .Take(MaxResults)
                .ToListAsync();
        }
    }

    public sealed record SourceOption(string Id, string Label, int UpcomingCount);
}
