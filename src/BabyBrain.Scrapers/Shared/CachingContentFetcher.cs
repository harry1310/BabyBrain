using Microsoft.Extensions.Logging;

namespace BabyBrain.Scrapers.Shared;

// IContentFetcher implementation: a persistent cache in front of an ordered
// chain of backends.
//
//   1. Unless this is a force-fresh run, return a cached copy newer than ttl.
//   2. Otherwise try each backend in order — the home laptop first (residential
//      IP, free), then ScraperAPI (paid fallback). A backend that throws is
//      skipped in favour of the next.
//   3. Cache whatever the winning backend returned, then return it.
//
// ScraperApiCreditsExhaustedException is never swallowed: there's no point
// trying further backends, and the caller must see that exact type to mark the
// run blocked (not failed) and skip raising a GitHub issue.
public sealed class CachingContentFetcher : IContentFetcher
{
    private readonly IFetchCache _cache;
    private readonly IReadOnlyList<IBackendFetcher> _backends;
    private readonly IScrapeCacheControl _control;
    private readonly ILogger<CachingContentFetcher> _logger;

    public CachingContentFetcher(
        IFetchCache cache,
        IEnumerable<IBackendFetcher> backends,
        IScrapeCacheControl control,
        ILogger<CachingContentFetcher> logger)
    {
        _cache = cache;
        _backends = backends.ToList();
        _control = control;
        _logger = logger;
    }

    public async Task<string> FetchAsync(string source, string url, TimeSpan ttl, bool renderJs = false, CancellationToken ct = default)
    {
        if (!_control.ForceFresh)
        {
            var cached = await _cache.GetAsync(url, renderJs, ttl, ct);
            if (cached is not null)
            {
                _logger.LogDebug("Fetch cache hit for {Url} (render={Render})", url, renderJs);
                return cached;
            }
        }

        // Only backends that can serve this request (e.g. the laptop backend
        // does plain GETs but not JS rendering).
        var eligible = _backends.Where(b => b.Supports(renderJs)).ToList();
        if (eligible.Count == 0)
            throw new InvalidOperationException(
                $"No content-fetch backend can serve {url} (renderJs={renderJs}).");

        Exception? lastError = null;
        for (var i = 0; i < eligible.Count; i++)
        {
            var backend = eligible[i];
            ct.ThrowIfCancellationRequested();
            try
            {
                var html = await backend.FetchAsync(url, renderJs, ct);
                await _cache.SetAsync(source, url, renderJs, html, backend.Name, ttl, ct);
                return html;
            }
            catch (ScraperApiCreditsExhaustedException)
            {
                throw; // terminal — see class comment.
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _logger.LogWarning(ex,
                    "Fetch backend '{Backend}' failed for {Url}; {Remaining} backend(s) left to try",
                    backend.Name, url, eligible.Count - i - 1);
            }
        }

        throw lastError ?? new InvalidOperationException($"All fetch backends failed for {url}");
    }
}
