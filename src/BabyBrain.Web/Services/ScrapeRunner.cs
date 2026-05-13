using BabyBrain.Scrapers;
using BabyBrain.Web.Data;

namespace BabyBrain.Web.Services;

public sealed class ScrapeRunner
{
    // Bigger than message-only so we keep the stack trace and inner exceptions
    // (useful for both human debugging and any future programmatic remediation).
    private const int ErrorMaxLength = 4000;

    // How far back we look to gauge "consecutive failures" and "was-broken-now-fixed".
    // 7 is enough for daily scrapes to see a week of context.
    private const int AlertHistoryWindow = 7;

    private readonly IScrapeStore _store;
    private readonly IEnumerable<IScraper> _scrapers;
    private readonly GeocodingService _geocoder;
    private readonly IScrapeAlertSink _alertSink;
    private readonly ILogger<ScrapeRunner> _logger;

    public ScrapeRunner(IScrapeStore store, IEnumerable<IScraper> scrapers, GeocodingService geocoder, IScrapeAlertSink alertSink, ILogger<ScrapeRunner> logger)
    {
        _store = store;
        _scrapers = scrapers;
        _geocoder = geocoder;
        _alertSink = alertSink;
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
            await NotifyAlertSinkAsync(scraper.SourceId, success: true, error: null, ct);
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

            await NotifyAlertSinkAsync(scraper.SourceId, success: false, error: detail, ct);
            return new ScraperOutcome(scraper.SourceId, false, 0, detail, completedAt);
        }
    }

    // Reads the just-recorded run plus its predecessors to decide whether this
    // scrape represents a streak entering alert territory or a fresh recovery.
    // Alerting failures are swallowed — they must never break the scrape.
    private async Task NotifyAlertSinkAsync(string sourceId, bool success, string? error, CancellationToken ct)
    {
        try
        {
            var history = await _store.GetRecentRunsAsync(sourceId, AlertHistoryWindow, ct);
            if (success)
            {
                // Newest-first: [0] is the current success. Fire recovery only if
                // the immediately previous run was a failure — otherwise we'd
                // re-alert on every subsequent success.
                if (history.Count > 1 && history[1].Status == ScrapeRun.StatusFailed)
                {
                    await _alertSink.OnRecoveryAsync(sourceId, ct);
                }
            }
            else
            {
                var streak = 0;
                foreach (var r in history)
                {
                    if (r.Status != ScrapeRun.StatusFailed) break;
                    streak++;
                }
                await _alertSink.OnFailureAsync(sourceId, error ?? "(no error captured)", streak, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Alert sink notification failed for {Source}", sourceId);
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
