using Microsoft.Playwright;

namespace BabyBrain.Scrapers.Shared;

// Centralises Chromium launch + sane defaults so every scraper goes through one
// browser context. The first call also installs the bundled Chromium build
// (no-op if already present).
public sealed class PlaywrightFetcher : IAsyncDisposable
{
    private static readonly SemaphoreSlim InstallGate = new(1, 1);
    private static bool _installed;

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private readonly IHtmlArchive _archive;

    // Optional browser channel ("chrome" to drive the installed Google Chrome
    // instead of Playwright's bundled Chromium) and headless toggle. The laptop
    // fetch service uses real Chrome because Cloudflare flags bundled headless
    // Chromium; the VPS scrapers keep the default (bundled, headless) since no
    // system Chrome is installed there.
    private readonly string? _channel;
    private readonly bool _headless;
    private readonly string[] _extraArgs;

    public PlaywrightFetcher() : this(NullHtmlArchive.Instance) { }

    public PlaywrightFetcher(IHtmlArchive archive, string? browserChannel = null, bool headless = true, string[]? extraArgs = null)
    {
        _archive = archive;
        _channel = browserChannel;
        _headless = headless;
        _extraArgs = extraArgs ?? Array.Empty<string>();
    }

    public PlaywrightFetcher(string? browserChannel, bool headless = true, string[]? extraArgs = null)
        : this(NullHtmlArchive.Instance, browserChannel, headless, extraArgs) { }

    // Default UA for rendered fetches. Overridable per-call via the userAgent
    // argument — some sites block specific UA strings (Bach to Baby's WAF 403s
    // a stock Chrome UA).
    private const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    // Default time to wait for waitForSelector. Overridable per-call: heavy
    // JS-rendered pages can need much longer on the small production VPS
    // (the British Museum detail pages, for one).
    private const int DefaultSelectorTimeoutMs = 30_000;

    public async Task<string> FetchRenderedHtmlAsync(
        string url,
        string waitForSelector,
        WaitForSelectorState waitState = WaitForSelectorState.Visible,
        CancellationToken ct = default,
        string? userAgent = null,
        int? selectorTimeoutMs = null,
        bool useNativeUserAgent = false)
    {
        await EnsureBrowserAsync(ct);

        // When useNativeUserAgent is set we deliberately leave UserAgent unset so
        // Chromium sends its own UA together with matching sec-ch-ua client hints.
        // Some WAFs (Bach to Baby's) now 403 any *spoofed* UA because the override
        // string disagrees with the client-hint headers; the native pair passes.
        var context = await _browser!.NewContextAsync(new()
        {
            UserAgent = useNativeUserAgent ? null : (userAgent ?? DefaultUserAgent),
            Locale = "en-GB",
        });
        try
        {
            var page = await context.NewPageAsync();
            // `Load` rather than NetworkIdle — sites with constant analytics chatter
            // (Southbank Centre, etc.) never reach NetworkIdle. The WaitForSelector
            // below is the real readiness check for the content we care about.
            await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.Load, Timeout = 60_000 });
            await page.WaitForSelectorAsync(waitForSelector, new()
            {
                Timeout = selectorTimeoutMs ?? DefaultSelectorTimeoutMs,
                State = waitState,
            });
            var html = await page.ContentAsync();
            await _archive.SaveAsync(url, html, ct);
            return html;
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    // Generic render with no known selector — used by the laptop fetch service,
    // which proxies arbitrary URLs. Loads the page in a real browser (which
    // clears a Cloudflare interstitial that a plain HTTP client can't), waits
    // for the challenge to drop and the DOM to settle, then returns the HTML.
    public async Task<string> FetchRenderedHtmlAsync(string url, CancellationToken ct = default)
    {
        await EnsureBrowserAsync(ct);

        // Native UA + client hints — a spoofed UA disagreeing with the hints is
        // a tell some Cloudflare configs flag.
        var context = await _browser!.NewContextAsync(new() { Locale = "en-GB" });
        // Mask the automation tell some Cloudflare configs check.
        await context.AddInitScriptAsync(
            "Object.defineProperty(navigator, 'webdriver', { get: () => undefined });");
        try
        {
            var page = await context.NewPageAsync();
            await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });

            await WaitForCloudflareAsync(page, ct);

            // Best-effort settle for client-side hydration (e.g. the British
            // Museum occurrence accordion). NetworkIdle never fires on some sites
            // (constant analytics), so cap it and fall through to a fixed wait.
            try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 5_000 }); }
            catch { /* analytics chatter — proceed */ }
            await page.WaitForTimeoutAsync(1_500);

            var html = await page.ContentAsync();
            await _archive.SaveAsync(url, html, ct);
            return html;
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    // Cloudflare's "Just a moment…" interstitial auto-navigates to the real page
    // once its JS executes in a genuine browser. Poll until the tell-tale markers
    // are gone (or give up after ~25s and return whatever's there).
    private static async Task WaitForCloudflareAsync(IPage page, CancellationToken ct)
    {
        for (var i = 0; i < 25; i++)
        {
            ct.ThrowIfCancellationRequested();
            var title = await page.TitleAsync();
            var content = await page.ContentAsync();
            var challenged =
                title.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("cf-browser-verification", StringComparison.OrdinalIgnoreCase);
            if (!challenged) return;
            await page.WaitForTimeoutAsync(1_000);
        }
    }

    private async Task EnsureBrowserAsync(CancellationToken ct)
    {
        if (_browser is not null) return;

        await InstallGate.WaitAsync(ct);
        try
        {
            // Only install bundled Chromium when we're going to use it. With a
            // system channel ("chrome") we drive the already-installed browser,
            // so skip the download.
            if (!_installed && _channel is null)
            {
                var exit = Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
                if (exit != 0) throw new InvalidOperationException($"Playwright install failed with exit code {exit}");
                _installed = true;
            }
        }
        finally
        {
            InstallGate.Release();
        }

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new()
        {
            Headless = _headless,
            Channel = _channel,
            // Drop the obvious "navigator.webdriver" automation tell, plus any
            // caller-supplied args (the laptop service passes --headless=new to
            // get Chrome's less-detectable new headless mode).
            Args = new[] { "--disable-blink-features=AutomationControlled" }.Concat(_extraArgs).ToArray(),
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) await _browser.DisposeAsync();
        _playwright?.Dispose();
    }
}
