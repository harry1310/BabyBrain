namespace BabyBrain.Scrapers.Shared;

// One way to actually retrieve a page (after the cache misses). The fetcher
// tries its backends in order — typically the home laptop (residential IP, free)
// then ScraperAPI (paid, always-on). A backend that throws is skipped in favour
// of the next; the last one's failure propagates.
public interface IBackendFetcher
{
    // Short identifier recorded on the cache entry for observability ("laptop",
    // "scraperapi").
    string Name { get; }

    // Whether this backend can serve the given request. The laptop backend does
    // plain GETs only, so it returns false for renderJs and the chain skips it
    // (no logged failure) and lets ScraperAPI handle the render.
    bool Supports(bool renderJs);

    Task<string> FetchAsync(string url, bool renderJs, CancellationToken ct = default);
}
