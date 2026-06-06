namespace BabyBrain.Scrapers.Shared;

// Persistent store of fetched HTML, keyed by (url, renderJs). Lives in the main
// DB so it survives restarts/redeploys. Lets the daily scrape reuse a recent
// fetch instead of re-paying ScraperAPI credits for an unchanged page.
public interface IFetchCache
{
    // The cached HTML if present and fetched within ttl; null on miss or when
    // staler than ttl.
    Task<string?> GetAsync(string url, bool renderJs, TimeSpan ttl, CancellationToken ct = default);

    // Upsert the fetched HTML for (url, renderJs). source labels it for admin
    // grouping/clearing; backend records which fetcher produced it.
    Task SetAsync(string source, string url, bool renderJs, string html, string backend, CancellationToken ct = default);

    // Admin: drop all cached fetches for a source so the next run refetches
    // live. Returns the number of entries removed.
    Task<int> ClearAsync(string source, CancellationToken ct = default);

    // Admin: per-source cache summary for the status page.
    Task<IReadOnlyList<FetchCacheStat>> GetStatsAsync(CancellationToken ct = default);
}

public sealed record FetchCacheStat(string Source, int Entries, DateTimeOffset? OldestFetchedAt, DateTimeOffset? NewestFetchedAt);
