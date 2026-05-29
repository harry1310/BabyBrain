using Microsoft.Extensions.Logging;

namespace BabyBrain.Scrapers.Shared;

// Proxies an HTTP GET through ScraperAPI (https://www.scraperapi.com/), which
// rotates residential IPs and solves the Cloudflare challenge on the way to
// the target. We need this because the British Museum and Southbank Centre
// sites Cloudflare-block every request from the Hetzner VPS regardless of
// User-Agent or TLS shape — verified end-to-end after PR #19 (curl from
// prod still returned 403; the body was a "Just a moment..." challenge
// page).
//
// The API key comes from BABYBRAIN_SCRAPERAPI_KEY, set in the container's
// env. A missing key throws at construction so a misconfigured deploy fails
// loud and early rather than at the first scrape.
public sealed class ScrapingApiFetcher
{
    // Default ScraperAPI endpoint. country_code=gb pins the residential
    // proxy to the UK so BM/SBC see a sensible Origin / Accept-Language and
    // don't redirect us to a localised variant.
    private const string EndpointBase = "https://api.scraperapi.com/";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly ILogger<ScrapingApiFetcher> _logger;

    public ScrapingApiFetcher(HttpClient http, string apiKey, ILogger<ScrapingApiFetcher> logger)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("ScraperAPI key is required", nameof(apiKey));
        _http = http;
        _apiKey = apiKey;
        _logger = logger;
    }

    public async Task<string> FetchAsync(string url, CancellationToken ct = default)
    {
        var proxied =
            $"{EndpointBase}?api_key={Uri.EscapeDataString(_apiKey)}" +
            $"&url={Uri.EscapeDataString(url)}" +
            "&country_code=gb";

        using var resp = await _http.GetAsync(proxied, ct);
        if (!resp.IsSuccessStatusCode)
        {
            // ScraperAPI surfaces upstream non-2xx as its own non-2xx; keep
            // the body in the exception so the GitHub issue carries the
            // actual reason (rate limit, bad key, upstream block).
            var body = await resp.Content.ReadAsStringAsync(ct);
            var preview = body.Length > 400 ? body[..400] + "…" : body;
            throw new InvalidOperationException(
                $"ScraperAPI fetch of {url} failed: {(int)resp.StatusCode} {resp.ReasonPhrase}. Body: {preview}");
        }

        var html = await resp.Content.ReadAsStringAsync(ct);
        _logger.LogDebug("ScraperAPI fetched {Url}: {Bytes} bytes", url, html.Length);
        return html;
    }
}
