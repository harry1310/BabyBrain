using BabyBrain.Scrapers.Domain;

namespace BabyBrain.Web.Data;

// Write-side abstraction over the SQLite DbContext for the scrape pipeline.
// ScrapeRunner depends on this rather than BabyBrainDbContext so the
// orchestration code is independent of the storage choice and easier to test.
// Reads are still done directly via DbContext on Razor pages — promote here
// only when there's a reason (testability, caching, replacing the backend).
public interface IScrapeStore
{
    // Upsert a scraper's emitted rows by ExternalKey + prune rows from the same
    // source that weren't seen this run.
    Task UpsertOccurrencesAsync(string sourceId, IReadOnlyList<EventOccurrence> rows, CancellationToken ct = default);

    // Record one per-scraper run for the Admin history view.
    Task RecordRunAsync(ScrapeRun run, CancellationToken ct = default);

    // Most-recent-first slice of past runs for a source. Used by ScrapeRunner
    // to detect failure streaks for the alerting hook.
    Task<IReadOnlyList<ScrapeRun>> GetRecentRunsAsync(string sourceId, int take, CancellationToken ct = default);
}
