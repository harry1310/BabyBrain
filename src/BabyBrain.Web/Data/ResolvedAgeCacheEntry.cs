namespace BabyBrain.Web.Data;

// One cached detail-page age probe per (Source, SourceUrl). Persisted in the
// main SQLite DB so the cache survives restarts and redeploys (the ./data
// volume), the same way event rows do. See IResolvedAgeCache.
public class ResolvedAgeCacheEntry
{
    public int Id { get; set; }

    public required string Source { get; set; }
    public required string SourceUrl { get; set; }

    // Both null = probed but the detail page carried no age guidance.
    public int? MinAgeMonths { get; set; }
    public int? MaxAgeMonths { get; set; }

    // When this probe was taken; the consumer's TTL is measured against it.
    public DateTimeOffset CheckedAt { get; set; }
}
