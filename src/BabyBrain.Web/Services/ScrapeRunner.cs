using BabyBrain.Scrapers;
using BabyBrain.Web.Data;

namespace BabyBrain.Web.Services;

public sealed class ScrapeRunner
{
    // Bigger than message-only so we keep the stack trace and inner exceptions
    // (useful for both human debugging and any future programmatic remediation).
    private const int ErrorMaxLength = 4000;

    private readonly IScrapeStore _store;
    private readonly IEnumerable<IScraper> _scrapers;
    private readonly GeocodingService _geocoder;
    private readonly ILogger<ScrapeRunner> _logger;

    public ScrapeRunner(IScrapeStore store, IEnumerable<IScraper> scrapers, GeocodingService geocoder, ILogger<ScrapeRunner> logger)
    {
        _store = store;
        _scrapers = scrapers;
        _geocoder = geocoder;
        _logger = logger;
    }

    public async Task<ScrapeResult> RunAllAsync(int horizonDays = 90, CancellationToken ct = default)
    {
        var outcomes = new List<ScraperOutcome>();
        foreach (var scraper in _scrapers)
        {
            ct.ThrowIfCancellationRequested();
            outcomes.Add(await RunOneAsync(scraper, horizonDays, ct));
        }
        return new ScrapeResult(outcomes, await GeocodeAsync(ct));
    }

    // Returns null when no scraper with that SourceId is registered.
    public async Task<ScrapeResult?> RunByIdAsync(string sourceId, int horizonDays = 90, CancellationToken ct = default)
    {
        var scraper = _scrapers.FirstOrDefault(s => s.SourceId == sourceId);
        if (scraper is null) return null;

        var outcome = await RunOneAsync(scraper, horizonDays, ct);
        return new ScrapeResult(new[] { outcome }, await GeocodeAsync(ct));
    }

    // Geocoding is shared across both paths. A failure here shouldn't undo any
    // successful scrape results already recorded.
    private async Task<int> GeocodeAsync(CancellationToken ct)
    {
        try { return await _geocoder.ResolveMissingAsync(ct); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Geocoding pass failed");
            return 0;
        }
    }

    private async Task<ScraperOutcome> RunOneAsync(IScraper scraper, int horizonDays, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            var rows = await scraper.ScrapeAsync(horizonDays);
            await _store.UpsertOccurrencesAsync(scraper.SourceId, rows, ct);

            var completedAt = DateTimeOffset.UtcNow;
            await _store.RecordRunAsync(new ScrapeRun
            {
                Source = scraper.SourceId,
                StartedAt = startedAt,
                CompletedAt = completedAt,
                Status = ScrapeRun.StatusSuccess,
                RowsScraped = rows.Count,
            }, ct);

            _logger.LogInformation("Scraped {Source}: {Count} rows", scraper.SourceId, rows.Count);
            return new ScraperOutcome(scraper.SourceId, true, rows.Count, null, completedAt);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var completedAt = DateTimeOffset.UtcNow;
            // Full exception (type + message + stack + inner chain) — much more
            // useful for diagnosis than just ex.Message.
            var detail = Truncate(ex.ToString(), ErrorMaxLength);
            _logger.LogError(ex, "Scraper {Source} failed", scraper.SourceId);

            try
            {
                await _store.RecordRunAsync(new ScrapeRun
                {
                    Source = scraper.SourceId,
                    StartedAt = startedAt,
                    CompletedAt = completedAt,
                    Status = ScrapeRun.StatusFailed,
                    RowsScraped = 0,
                    Error = detail,
                }, ct);
            }
            catch (Exception saveEx)
            {
                _logger.LogError(saveEx, "Failed to record ScrapeRun for {Source}", scraper.SourceId);
            }

            return new ScraperOutcome(scraper.SourceId, false, 0, detail, completedAt);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";
}

public sealed record ScrapeResult(IReadOnlyList<ScraperOutcome> Outcomes, int NewGeocodes)
{
    public bool AllSucceeded => Outcomes.All(o => o.Success);
    public int TotalRows => Outcomes.Where(o => o.Success).Sum(o => o.Rows);
    public IEnumerable<ScraperOutcome> Failures => Outcomes.Where(o => !o.Success);
}

public sealed record ScraperOutcome(string Source, bool Success, int Rows, string? Error, DateTimeOffset CompletedAt);
