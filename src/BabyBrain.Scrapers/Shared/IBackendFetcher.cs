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

    Task<string> FetchAsync(string url, bool renderJs, CancellationToken ct = default);
}
