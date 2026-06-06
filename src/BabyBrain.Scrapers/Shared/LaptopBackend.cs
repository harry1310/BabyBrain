namespace BabyBrain.Scrapers.Shared;

// Backend that fetches via a small service running on the user's home laptop
// (reachable over a reverse SSH tunnel). The laptop has a residential IP, so it
// isn't Cloudflare/datacenter-blocked the way the VPS is — and it costs nothing.
// Registered BEFORE ScrapingApiBackend so it's first choice; when the laptop is
// off the request fails fast (connection refused through the dead tunnel) and
// the chain falls back to ScraperAPI.
//
// The service renders with a real headless browser, so it can serve both plain
// and JS-rendered fetches (including the British Museum occurrence pages) — and
// clears the Cloudflare interstitial that blocks plain HTTP clients.
public sealed class LaptopBackend : IBackendFetcher
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _token;

    public LaptopBackend(HttpClient http, string baseUrl, string token)
    {
        _http = http;
        _baseUrl = baseUrl;
        _token = token;
    }

    public string Name => "laptop";

    public bool Supports(bool renderJs) => true;

    public async Task<string> FetchAsync(string url, bool renderJs, CancellationToken ct = default)
    {
        var requestUrl = $"{_baseUrl}?url={Uri.EscapeDataString(url)}&render={(renderJs ? "true" : "false")}";
        using var req = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        req.Headers.Add("X-Fetch-Token", _token);

        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }
}
