namespace BabyBrain.Scrapers.Shared;

// Per-scrape switch that lets an admin re-run bypass the fetch cache. It's a
// scoped service: the daily scheduled run leaves ForceFresh false (use the
// cache), while the Admin "Re-run" buttons set it true so the run refetches
// every page live and refreshes the cache. CachingContentFetcher reads it.
public interface IScrapeCacheControl
{
    bool ForceFresh { get; set; }
}

public sealed class ScrapeCacheControl : IScrapeCacheControl
{
    public bool ForceFresh { get; set; }
}
