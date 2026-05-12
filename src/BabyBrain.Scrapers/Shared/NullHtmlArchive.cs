namespace BabyBrain.Scrapers.Shared;

// No-op archive — used in tests, the spike harness, and production runs where
// BABYBRAIN_HTML_ARCHIVE_PATH isn't configured.
public sealed class NullHtmlArchive : IHtmlArchive
{
    public static readonly NullHtmlArchive Instance = new();

    public Task SaveAsync(string url, string html, CancellationToken ct = default) => Task.CompletedTask;
    public Task PruneAsync(int keepPerUrl, CancellationToken ct = default) => Task.CompletedTask;
}
