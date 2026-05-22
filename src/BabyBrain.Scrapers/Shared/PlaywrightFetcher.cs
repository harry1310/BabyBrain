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

    public PlaywrightFetcher() : this(NullHtmlArchive.Instance) { }

    public PlaywrightFetcher(IHtmlArchive archive) => _archive = archive;

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
        int? selectorTimeoutMs = null)
    {
        await EnsureBrowserAsync(ct);

        var context = await _browser!.NewContextAsync(new()
        {
            UserAgent = userAgent ?? DefaultUserAgent,
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

    private async Task EnsureBrowserAsync(CancellationToken ct)
    {
        if (_browser is not null) return;

        await InstallGate.WaitAsync(ct);
        try
        {
            if (!_installed)
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
        _browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true });
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) await _browser.DisposeAsync();
        _playwright?.Dispose();
    }
}
