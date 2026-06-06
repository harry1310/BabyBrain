namespace BabyBrain.Scrapers.Shared;

// The fetch entry point for sources that go through ScraperAPI. Wraps a
// persistent cache in front of a chain of backends (the home laptop first,
// ScraperAPI as fallback — see CachingContentFetcher). Scrapers depend on this
// instead of ScrapingApiFetcher directly so caching and the laptop fallback are
// transparent to them.
public interface IContentFetcher
{
    // Return url's HTML. A cached copy newer than ttl is reused unless a
    // force-fresh run is in effect (an admin re-run). renderJs asks the backend
    // to execute JavaScript first (needed for client-hydrated pages like the
    // British Museum occurrence accordion). source labels the cache entry so it
    // can be cleared/reported per scraper.
    Task<string> FetchAsync(string source, string url, TimeSpan ttl, bool renderJs = false, CancellationToken ct = default);
}

// Central cache lifetimes, so every ScraperAPI source ages its cache the same
// way. Listing/hub pages are kept fresher (new events surface sooner); detail
// pages (dates, age guidance) change slowly so they live longer.
public static class CacheTtl
{
    public static readonly TimeSpan Listing = TimeSpan.FromDays(2);
    public static readonly TimeSpan Detail = TimeSpan.FromDays(7);
}
