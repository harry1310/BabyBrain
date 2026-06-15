namespace BabyBrain.Scrapers.Shared;

// No-op cache — used by the spike harness and tests, where there's no database.
// Always misses, so every fetch goes to a backend; writes and admin ops are dropped.
public sealed class NullFetchCache : IFetchCache
{
    public static readonly NullFetchCache Instance = new();

    public Task<string?> GetAsync(string url, bool renderJs, TimeSpan ttl, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public Task SetAsync(string source, string url, bool renderJs, string html, string backend, TimeSpan ttl, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<int> ClearAsync(string source, CancellationToken ct = default) => Task.FromResult(0);

    public Task<IReadOnlyList<FetchCacheStat>> GetStatsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<FetchCacheStat>>(Array.Empty<FetchCacheStat>());
}
