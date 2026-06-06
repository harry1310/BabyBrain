namespace BabyBrain.Web.Data;

// One cached page fetch, keyed by (Url, RenderJs). Persisted in the main SQLite
// DB on the ./data volume so the cache survives restarts and redeploys. See
// IFetchCache / CachingContentFetcher.
public class FetchCacheEntry
{
    public int Id { get; set; }

    // The scraper that requested it — for admin grouping and per-source clear.
    public required string Source { get; set; }

    public required string Url { get; set; }

    // Whether this was a JS-rendered fetch; a page can be cached both ways.
    public bool RenderJs { get; set; }

    public required string Html { get; set; }

    // Which backend produced it ("laptop" / "scraperapi"), for observability.
    public required string Backend { get; set; }

    public DateTimeOffset FetchedAt { get; set; }
}
