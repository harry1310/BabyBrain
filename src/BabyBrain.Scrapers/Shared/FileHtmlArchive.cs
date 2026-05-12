using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace BabyBrain.Scrapers.Shared;

// Writes rendered HTML to a directory on disk. Filename shape:
//   {url-slug}_{yyyyMMddTHHmmss}.html
// The slug is host+path with non-alphanumerics collapsed to dashes, capped at
// 80 characters so we don't hit filesystem name-length limits.
public sealed class FileHtmlArchive : IHtmlArchive
{
    private readonly string _basePath;
    private readonly ILogger<FileHtmlArchive> _logger;

    public FileHtmlArchive(string basePath, ILogger<FileHtmlArchive> logger)
    {
        _basePath = basePath;
        _logger = logger;
        try { Directory.CreateDirectory(_basePath); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not create HTML archive directory {Path}", _basePath); }
    }

    public async Task SaveAsync(string url, string html, CancellationToken ct = default)
    {
        try
        {
            var slug = Slugify(url);
            var ts = DateTime.UtcNow.ToString("yyyyMMddTHHmmss");
            var path = Path.Combine(_basePath, $"{slug}_{ts}.html");
            await File.WriteAllTextAsync(path, html, ct);
        }
        catch (Exception ex)
        {
            // Archive failures must not affect the scrape — log and swallow.
            _logger.LogWarning(ex, "Failed to archive HTML for {Url}", url);
        }
    }

    public Task PruneAsync(int keepPerUrl, CancellationToken ct = default)
    {
        try
        {
            if (!Directory.Exists(_basePath)) return Task.CompletedTask;
            var groups = Directory.EnumerateFiles(_basePath, "*.html")
                .Select(p => new FileInfo(p))
                .GroupBy(f => SlugFromFilename(f.Name));
            foreach (var group in groups)
            {
                foreach (var stale in group.OrderByDescending(f => f.LastWriteTimeUtc).Skip(keepPerUrl))
                {
                    try { stale.Delete(); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Could not delete {Path}", stale.FullName); }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HTML archive prune failed");
        }
        return Task.CompletedTask;
    }

    private static readonly Regex SlugCleanup = new(@"[^a-z0-9]+", RegexOptions.Compiled);
    private static readonly Regex FilenameTailTimestamp = new(@"_\d{8}T\d{6}\.html$", RegexOptions.Compiled);

    private static string Slugify(string url)
    {
        string raw;
        try { var u = new Uri(url); raw = u.Host + u.AbsolutePath; }
        catch (UriFormatException) { raw = url; }
        var slug = SlugCleanup.Replace(raw.ToLowerInvariant(), "-").Trim('-');
        return slug.Length > 80 ? slug[..80] : slug;
    }

    private static string SlugFromFilename(string filename)
    {
        // Strip the trailing _yyyyMMddTHHmmss.html to recover the slug portion.
        return FilenameTailTimestamp.Replace(filename, "");
    }
}
