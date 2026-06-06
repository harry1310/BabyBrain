namespace BabyBrain.Web.Data;

// One row per per-scraper run. Both the manual "Run all scrapers now" button
// and the daily background service write rows here so the Admin page can
// surface recent history alongside the current attempt.
public class ScrapeRun
{
    public int Id { get; set; }

    // Matches IScraper.SourceId so we can group history per scraper.
    public required string Source { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }

    // "success", "failed", or "blocked". String rather than enum so adding
    // states later doesn't need a migration. "blocked" = an upstream billing
    // state (ScraperAPI credits exhausted), not a scraper fault — surfaced with
    // its own admin symbol and deliberately not alerted as a GitHub issue.
    public required string Status { get; set; }

    // Rows produced by the scraper for this run. Zero on failure.
    public int RowsScraped { get; set; }

    // Set when Status == "failed". Truncated to a reasonable length so a giant
    // stack trace doesn't blow up the DB.
    public string? Error { get; set; }

    public const string StatusSuccess = "success";
    public const string StatusFailed = "failed";
    public const string StatusBlocked = "blocked";
}
