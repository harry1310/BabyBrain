using BabyBrain.Scrapers.Shared;
using Microsoft.EntityFrameworkCore;

namespace BabyBrain.Web.Data;

// SQLite-backed fetch cache. Keyed by (Url, RenderJs); the TTL comparison is
// done in memory after fetching by key, since FetchedAt is a stringly-typed
// DateTimeOffset whose aggregate/compare translation we'd rather not lean on.
public sealed class EfFetchCache : IFetchCache
{
    private readonly BabyBrainDbContext _db;

    public EfFetchCache(BabyBrainDbContext db) => _db = db;

    public async Task<string?> GetAsync(string url, bool renderJs, TimeSpan ttl, CancellationToken ct = default)
    {
        var entry = await _db.FetchCache
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Url == url && e.RenderJs == renderJs, ct);

        if (entry is null || entry.FetchedAt < DateTimeOffset.UtcNow - ttl)
            return null;

        return entry.Html;
    }

    public async Task SetAsync(string source, string url, bool renderJs, string html, string backend, TimeSpan ttl, CancellationToken ct = default)
    {
        var entry = await _db.FetchCache
            .FirstOrDefaultAsync(e => e.Url == url && e.RenderJs == renderJs, ct);

        if (entry is null)
        {
            entry = new FetchCacheEntry
            {
                Source = source, Url = url, RenderJs = renderJs, Html = html, Backend = backend,
            };
            _db.FetchCache.Add(entry);
        }
        else
        {
            entry.Source = source;
            entry.Html = html;
            entry.Backend = backend;
        }

        entry.FetchedAt = DateTimeOffset.UtcNow;
        entry.TtlSeconds = (long)ttl.TotalSeconds;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<int> ClearAsync(string source, CancellationToken ct = default)
        => await _db.FetchCache.Where(e => e.Source == source).ExecuteDeleteAsync(ct);

    public async Task<IReadOnlyList<FetchCacheStat>> GetStatsAsync(CancellationToken ct = default)
    {
        // Project only the small columns (skip the HTML blobs) and aggregate in
        // memory — the cache is a handful of rows per source.
        var rows = await _db.FetchCache
            .AsNoTracking()
            .Select(e => new { e.Source, e.FetchedAt, e.TtlSeconds })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.Source)
            .Select(g => new FetchCacheStat(
                g.Key,
                g.Count(),
                g.Min(r => (DateTimeOffset?)r.FetchedAt),
                g.Max(r => (DateTimeOffset?)r.FetchedAt),
                g.GroupBy(r => ClassifyKind(r.TtlSeconds))
                    .Select(kg => new FetchCacheKindStat(
                        kg.Key,
                        kg.Count(),
                        kg.Min(r => Expiry(r.FetchedAt, r.TtlSeconds)),
                        kg.Max(r => Expiry(r.FetchedAt, r.TtlSeconds))))
                    .OrderBy(k => KindRank(k.Kind))
                    .ToList()))
            .OrderBy(s => s.Source)
            .ToList();
    }

    // Entries are labelled by the TTL they were fetched under: the shorter
    // CacheTtl.Listing window → "listing", anything longer → "detail". 0 marks a
    // legacy row written before TtlSeconds existed.
    private static string ClassifyKind(long ttlSeconds)
    {
        if (ttlSeconds <= 0) return "unknown";
        return TimeSpan.FromSeconds(ttlSeconds) <= CacheTtl.Listing ? "listing" : "detail";
    }

    private static int KindRank(string kind) => kind switch
    {
        "listing" => 0,
        "detail" => 1,
        _ => 2,
    };

    private static DateTimeOffset? Expiry(DateTimeOffset fetchedAt, long ttlSeconds)
        => ttlSeconds <= 0 ? null : fetchedAt + TimeSpan.FromSeconds(ttlSeconds);
}
