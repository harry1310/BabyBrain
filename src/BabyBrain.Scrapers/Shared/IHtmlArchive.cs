namespace BabyBrain.Scrapers.Shared;

// Persistent record of rendered HTML per scrape, so a failed run is debuggable
// after the fact (and so any future programmatic remediation has the page state
// to work from). Failures inside an archive implementation MUST NOT propagate —
// archiving is a debugging convenience, never a correctness dependency.
public interface IHtmlArchive
{
    // Save one rendered HTML snapshot. Implementations key on URL + timestamp
    // and decide their own on-disk layout.
    Task SaveAsync(string url, string html, CancellationToken ct = default);

    // Trim retained snapshots down to the most recent `keepPerUrl` per URL slug.
    // Called by the daily scheduler; safe to call ad-hoc.
    Task PruneAsync(int keepPerUrl, CancellationToken ct = default);
}
