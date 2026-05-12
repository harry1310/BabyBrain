using BabyBrain.Scrapers;
using BabyBrain.Web.Data;

namespace BabyBrain.Web.Services;

public sealed class ScrapeRunner
{
    private readonly BabyBrainDbContext _db;
    private readonly IEnumerable<IScraper> _scrapers;
    private readonly GeocodingService _geocoder;
    private readonly ILogger<ScrapeRunner> _logger;

    public ScrapeRunner(BabyBrainDbContext db, IEnumerable<IScraper> scrapers, GeocodingService geocoder, ILogger<ScrapeRunner> logger)
    {
        _db = db;
        _scrapers = scrapers;
        _geocoder = geocoder;
        _logger = logger;
    }

    public async Task<ScrapeResult> RunAllAsync(int horizonDays = 90, CancellationToken ct = default)
    {
        var perSource = new List<string>();
        foreach (var scraper in _scrapers)
        {
            ct.ThrowIfCancellationRequested();
            var rows = await scraper.ScrapeAsync(horizonDays: horizonDays);
            await EventOccurrenceUpsert.ApplyAsync(_db, scraper.SourceId, rows);
            perSource.Add($"{scraper.SourceId}: {rows.Count}");
            _logger.LogInformation("Scraped {Source}: {Count} rows", scraper.SourceId, rows.Count);
        }
        var geocoded = await _geocoder.ResolveMissingAsync(_db, ct);
        var geoSuffix = geocoded > 0 ? $". Geocoded {geocoded} new postcodes." : ".";
        var summary = "Scraped " + string.Join("; ", perSource) + geoSuffix;
        return new ScrapeResult(summary, geocoded);
    }
}

public readonly record struct ScrapeResult(string Summary, int NewGeocodes);
