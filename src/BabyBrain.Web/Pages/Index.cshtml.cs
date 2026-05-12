using BabyBrain.Scrapers.Domain;
using BabyBrain.Web.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BabyBrain.Web.Pages;

public class IndexModel : PageModel
{
    private readonly BabyBrainDbContext _db;
    public IndexModel(BabyBrainDbContext db) => _db = db;

    public SearchFilter Filter { get; private set; } = new();
    public List<EventOccurrence> Results { get; private set; } = new();
    public Dictionary<string, (double Lat, double Lng)> PostcodeCoords { get; private set; } = new();

    public async Task OnGetAsync(string? range, DateOnly? date, int? ageMonths)
    {
        Filter = new SearchFilter
        {
            Range = range ?? "today",
            SpecificDate = date,
            AgeMonths = ageMonths,
        };

        var (from, to) = ResolveDateWindow(Filter);

        var q = _db.EventOccurrences.AsQueryable()
            .Where(e => e.Date >= from && e.Date <= to);

        if (Filter.AgeMonths is int age)
        {
            // Match events whose declared range includes the child's age.
            // Events with no age data are kept (we can't exclude them safely yet).
            q = q.Where(e =>
                (e.MinAgeMonths == null && e.MaxAgeMonths == null) ||
                ((e.MinAgeMonths == null || e.MinAgeMonths <= age) &&
                 (e.MaxAgeMonths == null || e.MaxAgeMonths >= age)));
        }

        Results = await q.OrderBy(e => e.Date).ThenBy(e => e.StartTime).Take(500).ToListAsync();

        // Pull geocodes for postcodes in the result set so the map can pin them.
        var postcodes = Results
            .Where(r => !string.IsNullOrEmpty(r.Postcode))
            .Select(r => Data.Geocode.Normalise(r.Postcode!))
            .Distinct()
            .ToList();
        if (postcodes.Count > 0)
        {
            var geos = await _db.Geocodes.Where(g => postcodes.Contains(g.Postcode)).ToListAsync();
            PostcodeCoords = geos.ToDictionary(g => g.Postcode, g => (g.Latitude, g.Longitude));
        }
    }

    private static (DateOnly from, DateOnly to) ResolveDateWindow(SearchFilter f)
    {
        var today = DateOnly.FromDateTime(DateTime.Now.Date);
        return f.Range switch
        {
            "tomorrow" => (today.AddDays(1), today.AddDays(1)),
            "week" => (today, today.AddDays(6)),
            "custom" when f.SpecificDate is { } d => (d, d),
            _ => (today, today),
        };
    }

    public class SearchFilter
    {
        public string Range { get; set; } = "today";
        public DateOnly? SpecificDate { get; set; }
        public int? AgeMonths { get; set; }
    }
}
