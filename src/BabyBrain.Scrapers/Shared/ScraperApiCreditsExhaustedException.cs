namespace BabyBrain.Scrapers.Shared;

// Thrown when ScraperAPI rejects a request because the monthly credit balance
// is spent (HTTP 403, body "You have exhausted the API Credits…"). This is a
// billing state, not a scraper defect, so callers treat it specially: the run
// is marked blocked rather than failed, and no claude-fix GitHub issue is filed.
public sealed class ScraperApiCreditsExhaustedException : Exception
{
    public ScraperApiCreditsExhaustedException(string message) : base(message) { }
}
