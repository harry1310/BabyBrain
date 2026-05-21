using BabyBrain.Scrapers.Domain;
using BabyBrain.Web.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BabyBrain.Web.Pages;

public class IndexModel : PageModel
{
    // "Today" for our users means today in London, not on the server. The
    // production container runs UTC, so DateTime.Now there is UTC, which
    // would slip the search window by a day for a user opening the site
    // just after midnight BST. Pin to Europe/London.
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    private readonly BabyBrainDbContext _db;
    private readonly GeocodingService _geocoder;

    public IndexModel(BabyBrainDbContext db, GeocodingService geocoder)
    {
        _db = db;
        _geocoder = geocoder;
    }

    public SearchFilter Filter { get; private set; } = new();
    public List<EventOccurrence> Results { get; private set; } = new();
    public Dictionary<string, (double Lat, double Lng)> PostcodeCoords { get; private set; } = new();

    // Shown only when a distance filter was requested but couldn't be applied
    // (bad postcode, or no origin given). Null when the filter applied fine —
    // the results themselves are the confirmation.
    public string? DistanceNote { get; private set; }

    public async Task OnGetAsync(
        string? range, DateOnly? date, int? ageMonths,
        double? radius, string? originPostcode, double? originLat, double? originLng,
        CancellationToken ct = default)
    {
        Filter = new SearchFilter
        {
            Range = range ?? "today",
            SpecificDate = date,
            AgeMonths = ageMonths,
            Radius = radius,
            OriginPostcode = originPostcode,
            OriginLat = originLat,
            OriginLng = originLng,
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

        Results = await q.OrderBy(e => e.Date).ThenBy(e => e.StartTime).Take(500).ToListAsync(ct);

        // Pull geocodes for postcodes in the result set so the map can pin them
        // — and so the distance filter below can measure them.
        var postcodes = Results
            .Where(r => !string.IsNullOrEmpty(r.Postcode))
            .Select(r => Data.Geocode.Normalise(r.Postcode!))
            .Distinct()
            .ToList();
        if (postcodes.Count > 0)
        {
            var geos = await _db.Geocodes.Where(g => postcodes.Contains(g.Postcode)).ToListAsync(ct);
            PostcodeCoords = geos.ToDictionary(g => g.Postcode, g => (g.Latitude, g.Longitude));
        }

        await ApplyDistanceFilterAsync(ct);
    }

    // Narrows Results to events within Filter.Radius miles of the visitor's
    // origin. The origin is either browser-geolocation coordinates or a typed
    // postcode; coordinates win when both are present. Events with no locatable
    // postcode are dropped while a distance filter is active.
    private async Task ApplyDistanceFilterAsync(CancellationToken ct)
    {
        if (Filter.Radius is not double miles) return;

        (double Lat, double Lng)? origin = null;
        if (Filter.OriginLat is double lat && Filter.OriginLng is double lng)
        {
            origin = (lat, lng);
        }
        else if (!string.IsNullOrWhiteSpace(Filter.OriginPostcode))
        {
            origin = await _geocoder.GeocodeOneAsync(Filter.OriginPostcode, ct);
            if (origin is null)
                DistanceNote = "We couldn't find that postcode, so we're showing every area.";
        }
        else
        {
            DistanceNote = "Enter a postcode or use your location to filter by distance.";
        }

        if (origin is not { } o) return;

        Results = Results
            .Where(r =>
            {
                if (string.IsNullOrEmpty(r.Postcode)) return false;
                if (!PostcodeCoords.TryGetValue(Data.Geocode.Normalise(r.Postcode), out var c))
                    return false;
                return Data.Geocode.DistanceMiles(o.Lat, o.Lng, c.Lat, c.Lng) <= miles;
            })
            .ToList();
    }

    private static (DateOnly from, DateOnly to) ResolveDateWindow(SearchFilter f)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, London).Date);
        switch (f.Range)
        {
            case "tomorrow":
                return (today.AddDays(1), today.AddDays(1));
            case "week":
            {
                // Today through the coming Sunday (just today, if today is Sunday).
                var toSunday = ((int)DayOfWeek.Sunday - (int)today.DayOfWeek + 7) % 7;
                return (today, today.AddDays(toSunday));
            }
            case "nextweek":
            {
                // Next Monday through the Sunday after it.
                var toMonday = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
                if (toMonday == 0) toMonday = 7; // today IS Monday → jump to next week's
                var nextMonday = today.AddDays(toMonday);
                return (nextMonday, nextMonday.AddDays(6));
            }
            case "custom" when f.SpecificDate is { } d:
                return (d, d);
            default:
                return (today, today);
        }
    }

    public class SearchFilter
    {
        public string Range { get; set; } = "today";
        public DateOnly? SpecificDate { get; set; }
        public int? AgeMonths { get; set; }

        // Distance filter. Radius is in miles; null means "any distance".
        // Origin is browser coordinates, or a postcode geocoded server-side.
        public double? Radius { get; set; }
        public string? OriginPostcode { get; set; }
        public double? OriginLat { get; set; }
        public double? OriginLng { get; set; }
    }
}
