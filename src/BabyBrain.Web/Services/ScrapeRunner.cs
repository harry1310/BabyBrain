using BabyBrain.Scrapers;
using BabyBrain.Scrapers.Shared;
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
    private readonly IScrapeCacheControl _cacheControl;
    private readonly ILogger<ScrapeRunner> _logger;

    public ScrapeRunner(IScrapeStore store, IEnumerable<IScraper> scrapers, GeocodingService geocoder, IScrapeAlertSink alertSink, IScrapeCacheControl cacheControl, ILogger<ScrapeRunner> logger)
    {
        _store = store;
        _scrapers = scrapers;
        _geocoder = geocoder;
        _alertSink = alertSink;
        _cacheControl = cacheControl;
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

    // Returns null when no scraper with that SourceId is registered. forceFresh
    // bypasses the fetch cache for this run (set by the Admin "Re-run" buttons),
    // so a manual re-run always fetches live and refreshes the cache.
    public async Task<ScrapeResult?> RunByIdAsync(string sourceId, bool forceFresh = false, int horizonDays = 90, CancellationToken ct = default)
    {
        var scraper = _scrapers.FirstOrDefault(s => s.SourceId == sourceId);
        if (scraper is null) return null;

        _cacheControl.ForceFresh = forceFresh;
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

            // A scrape that completes but returns nothing is almost always a
            // broken selector, not a genuinely empty source. Treat it as a
            // failure — and crucially skip the upsert: it prunes rows the
            // scrape didn't return, so upserting an empty result would wipe
            // every existing row for this source.
            if (rows.Count == 0)
            {
                const string emptyError = "Scraper completed but returned 0 events — treated as a failure.";
                var emptyAt = DateTimeOffset.UtcNow;
                _logger.LogWarning("Scraper {Source} returned 0 rows — treated as failure", scraper.SourceId);

                await TryRecordRunAsync(new ScrapeRun
                {
                    Source = scraper.SourceId,
                    StartedAt = startedAt,
                    CompletedAt = emptyAt,
                    Status = ScrapeRun.StatusFailed,
                    RowsScraped = 0,
                    Error = emptyError,
                }, ct);

                await NotifyAlertSinkAsync(scraper.SourceId, success: false, error: emptyError, ct);
                return new ScraperOutcome(scraper.SourceId, false, 0, emptyError, emptyAt);
            }

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
        // Only treat this as a real cancellation (host shutdown / ct tripped)
        // when the token actually fired. Otherwise it's almost always an
        // HttpClient *timeout* (TaskCanceledException : OperationCanceledException)
        // from a slow/flaky site — which must be recorded as a failure (and show
        // a ✗) rather than silently escaping unrecorded.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ScraperApiCreditsExhaustedException ex)
        {
            // Billing state, not a scraper fault: record a distinct "blocked"
            // status and deliberately skip the alert sink so no claude-fix issue
            // is raised. Clears itself on the next run once credits return.
            var completedAt = DateTimeOffset.UtcNow;
            var detail = Truncate(ex.Message, ErrorMaxLength);
            _logger.LogWarning("Scraper {Source} blocked: ScraperAPI credits exhausted", scraper.SourceId);

            await TryRecordRunAsync(new ScrapeRun
            {
                Source = scraper.SourceId,
                StartedAt = startedAt,
                CompletedAt = completedAt,
                Status = ScrapeRun.StatusBlocked,
                RowsScraped = 0,
                Error = detail,
            }, ct);

            return new ScraperOutcome(scraper.SourceId, false, 0, detail, completedAt);
        }
        catch (SourceEmptyException ex)
        {
            // The scraper reached its source and parsed it cleanly, but it
            // genuinely has no events matching our criteria right now (not a
            // defect). Record a 0-row *success* so any open claude-fix issue
            // auto-closes via the recovery path, and deliberately skip the
            // upsert: with no rows there is nothing to write, and upserting an
            // empty set would prune every existing row for this source.
            var completedAt = DateTimeOffset.UtcNow;
            _logger.LogInformation(
                "Scraper {Source} reached source but is legitimately empty: {Detail}",
                scraper.SourceId, ex.Message);

            await TryRecordRunAsync(new ScrapeRun
            {
                Source = scraper.SourceId,
                StartedAt = startedAt,
                CompletedAt = completedAt,
                Status = ScrapeRun.StatusSuccess,
                RowsScraped = 0,
            }, ct);

            await NotifyAlertSinkAsync(scraper.SourceId, success: true, error: null, ct);
            return new ScraperOutcome(scraper.SourceId, true, 0, null, completedAt);
        }
        catch (Exception ex)
        {
            var completedAt = DateTimeOffset.UtcNow;
            // Full exception (type + message + stack + inner chain) — much more
            // useful for diagnosis than just ex.Message.
            var detail = Truncate(ex.ToString(), ErrorMaxLength);
            _logger.LogError(ex, "Scraper {Source} failed", scraper.SourceId);

            await TryRecordRunAsync(new ScrapeRun
            {
                Source = scraper.SourceId,
                StartedAt = startedAt,
                CompletedAt = completedAt,
                Status = ScrapeRun.StatusFailed,
                RowsScraped = 0,
                Error = detail,
            }, ct);

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
                // the previous *decisive* run was a failure — otherwise we'd
                // re-alert on every subsequent success. 'blocked' (billing) runs
                // neither raise nor close issues, so skip them: a
                // failed → blocked → success sequence should still close the
                // original issue.
                var priorDecisive = history.Skip(1).FirstOrDefault(r => r.Status != ScrapeRun.StatusBlocked);
                if (priorDecisive is not null && priorDecisive.Status == ScrapeRun.StatusFailed)
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

    // Records a run, swallowing storage errors — failing to log the run must
    // never mask the scrape outcome we're trying to report.
    private async Task TryRecordRunAsync(ScrapeRun run, CancellationToken ct)
    {
        try { await _store.RecordRunAsync(run, ct); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record ScrapeRun for {Source}", run.Source);
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
