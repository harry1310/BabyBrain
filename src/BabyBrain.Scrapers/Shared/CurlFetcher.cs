using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace BabyBrain.Scrapers.Shared;

// Shells out to the system `curl` binary. We use this instead of HttpClient
// for the British Museum and Southbank Centre hub pages, where Cloudflare
// 403s every .NET HttpClient request regardless of headers — confirmed by
// the spike's --bm-http-probe. curl uses OpenSSL, whose TLS fingerprint
// (JA3/JA4) Cloudflare currently waves through; .NET's SocketsHttpHandler
// has a different fingerprint that gets flagged as automation.
//
// Both sites server-side render their content when given a browser User-
// Agent — the response body has the events directly, no JS required. So a
// successful curl gives us the same HTML Playwright would, in a fraction of
// the time, and avoids the WaitForSelector timeout failures the slow
// production VPS kept hitting (issues #15, #17, #18).
//
// curl is available on the Hetzner Docker image and on Windows 10+ out of
// the box, so no extra deps needed.
public sealed class CurlFetcher
{
    private const string ChromeUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private readonly ILogger<CurlFetcher> _logger;

    public CurlFetcher(ILogger<CurlFetcher> logger) => _logger = logger;

    public async Task<string> FetchAsync(string url, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var psi = new ProcessStartInfo("curl")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // ArgumentList builds argv directly — no shell escaping needed, so
        // a hostile URL can't smuggle extra flags.
        psi.ArgumentList.Add("--silent");
        psi.ArgumentList.Add("--show-error");
        psi.ArgumentList.Add("--fail");                // non-2xx -> non-zero exit
        psi.ArgumentList.Add("--location");            // follow redirects
        psi.ArgumentList.Add("--max-time");
        psi.ArgumentList.Add(((int)DefaultTimeout.TotalSeconds).ToString());
        psi.ArgumentList.Add("--user-agent");
        psi.ArgumentList.Add(ChromeUa);
        psi.ArgumentList.Add(url);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start curl");

        // Read stdout/stderr concurrently — a large body filling stdout's
        // pipe buffer would otherwise deadlock WaitForExit.
        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw;
        }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"curl exit {p.ExitCode} fetching {url}: {stderr.Trim()}");
        }

        _logger.LogDebug("curl {Url} -> {Bytes} bytes in {Elapsed}ms",
            url, stdout.Length, sw.ElapsedMilliseconds);
        return stdout;
    }
}
