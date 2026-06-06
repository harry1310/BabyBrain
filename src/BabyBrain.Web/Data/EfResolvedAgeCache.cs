using BabyBrain.Scrapers.Shared;
using Microsoft.EntityFrameworkCore;

namespace BabyBrain.Web.Data;

// Stores resolved-age probes in the main SQLite DB so the cache survives
// restarts and redeploys (the ./data volume), the same durability as event rows.
public sealed class EfResolvedAgeCache : IResolvedAgeCache
{
    private readonly BabyBrainDbContext _db;

    public EfResolvedAgeCache(BabyBrainDbContext db) => _db = db;

    public async Task<AgeProbe?> TryGetAsync(string source, string sourceUrl, TimeSpan maxAge, CancellationToken ct = default)
    {
        // Fetch by the (string) key, then compare the timestamp in memory — the
        // CheckedAt column is a stringly-typed DateTimeOffset, so doing the TTL
        // comparison here avoids leaning on its LINQ translation.
        var entry = await _db.ResolvedAgeCache
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Source == source && e.SourceUrl == sourceUrl, ct);

        if (entry is null || entry.CheckedAt < DateTimeOffset.UtcNow - maxAge)
            return null;

        return new AgeProbe(entry.MinAgeMonths, entry.MaxAgeMonths);
    }

    public async Task SetAsync(string source, string sourceUrl, int? minAgeMonths, int? maxAgeMonths, CancellationToken ct = default)
    {
        var entry = await _db.ResolvedAgeCache
            .FirstOrDefaultAsync(e => e.Source == source && e.SourceUrl == sourceUrl, ct);

        if (entry is null)
        {
            entry = new ResolvedAgeCacheEntry { Source = source, SourceUrl = sourceUrl };
            _db.ResolvedAgeCache.Add(entry);
        }

        entry.MinAgeMonths = minAgeMonths;
        entry.MaxAgeMonths = maxAgeMonths;
        entry.CheckedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }
}
