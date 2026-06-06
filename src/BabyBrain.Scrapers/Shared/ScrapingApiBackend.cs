namespace BabyBrain.Scrapers.Shared;

// Backend that fetches via ScraperAPI. Always-on but paid, so it sits last in
// the chain — the fallback used when the home laptop isn't running.
public sealed class ScrapingApiBackend : IBackendFetcher
{
    private readonly ScrapingApiFetcher _api;

    public ScrapingApiBackend(ScrapingApiFetcher api) => _api = api;

    public string Name => "scraperapi";

    public Task<string> FetchAsync(string url, bool renderJs, CancellationToken ct = default)
        => _api.FetchAsync(url, ct, renderJs);
}
