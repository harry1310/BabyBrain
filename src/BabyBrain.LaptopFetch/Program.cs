using System.Security.Cryptography;
using System.Text;
using BabyBrain.Scrapers.Shared;

// BabyBrain laptop fetch service.
//
// Runs on the user's home laptop and fetches pages with a REAL headless browser
// (Chromium via Playwright). The two paid sources (British Museum, Southbank)
// sit behind a Cloudflare interstitial that challenges any plain HTTP client —
// even from a residential IP — so a genuine browser is required to clear it
// (and, as a bonus, it renders British Museum's JS-hydrated occurrence list).
//
// The VPS reaches this over a reverse SSH tunnel and uses it as first-choice
// fetch backend, falling back to ScraperAPI when the laptop is off (see
// docs/laptop-fetch.md). Bound to 127.0.0.1 so it's reachable only through the
// tunnel; every request must carry the shared token.

var token = Environment.GetEnvironmentVariable("BABYBRAIN_LAPTOP_FETCH_TOKEN");
if (string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine("BABYBRAIN_LAPTOP_FETCH_TOKEN must be set.");
    return 1;
}

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);

// One shared browser for the process, driving the installed Google Chrome
// (channel "chrome") rather than bundled Chromium — Cloudflare flags headless
// bundled Chromium but lets real Chrome through. PlaywrightFetcher opens a fresh
// context per request.
// Real Chrome, NON-headless (a visible window). Verified necessary: Cloudflare's
// managed challenge fingerprints and blocks every headless variant (bundled,
// real-Chrome, new-headless+stealth) but lets a genuine visible browser through.
// Requires an interactive, logged-in desktop session on the laptop.
builder.Services.AddSingleton(_ => new PlaywrightFetcher(browserChannel: "chrome", headless: false));

var app = builder.Build();

app.MapGet("/healthz", () => Results.Text("ok"));

app.MapGet("/fetch", async (
    string? url, HttpContext ctx, PlaywrightFetcher fetcher,
    ILoggerFactory loggerFactory, CancellationToken ct) =>
{
    var log = loggerFactory.CreateLogger("fetch");

    if (!TokenMatches(ctx, token))
        return Results.StatusCode(StatusCodes.Status401Unauthorized);

    if (string.IsNullOrWhiteSpace(url) ||
        !Uri.TryCreate(url, UriKind.Absolute, out var target) ||
        (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
    {
        return Results.BadRequest("A valid absolute http(s) url is required.");
    }

    try
    {
        // The browser renders everything, so the caller's render flag is moot —
        // we always return the fully-rendered DOM.
        var html = await fetcher.FetchRenderedHtmlAsync(target.ToString(), ct);
        log.LogInformation("fetched {Url} ({Bytes} bytes)", target, html.Length);
        return Results.Text(html, "text/html; charset=utf-8");
    }
    catch (Exception ex)
    {
        log.LogWarning(ex, "fetch failed for {Url}", target);
        return Results.StatusCode(StatusCodes.Status502BadGateway);
    }
});

// Loopback only — reachable solely through the reverse SSH tunnel.
app.Run("http://127.0.0.1:8099");
return 0;

// Constant-time token comparison so the check doesn't leak length/contents via timing.
static bool TokenMatches(HttpContext ctx, string expected)
{
    var provided = ctx.Request.Headers["X-Fetch-Token"].ToString();
    if (string.IsNullOrEmpty(provided)) return false;
    return CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected));
}
