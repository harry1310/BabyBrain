namespace BabyBrain.Scrapers.Shared;

// No-op cache — used by the spike harness and tests, where there's no database.
// Always reports a miss, so callers always do the live probe (and Set is dropped).
public sealed class NullResolvedAgeCache : IResolvedAgeCache
{
    public static readonly NullResolvedAgeCache Instance = new();

    public Task<AgeProbe?> TryGetAsync(string source, string sourceUrl, TimeSpan maxAge, CancellationToken ct = default)
        => Task.FromResult<AgeProbe?>(null);

    public Task SetAsync(string source, string sourceUrl, int? minAgeMonths, int? maxAgeMonths, CancellationToken ct = default)
        => Task.CompletedTask;
}
