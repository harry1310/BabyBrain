namespace BabyBrain.Scrapers.Shared;

// Thrown when a scraper reaches its source successfully and parses it cleanly,
// but the source genuinely has no events matching our criteria right now (e.g.
// every listed event is school-age, or all dates fall outside the horizon).
// This is a legitimate empty result, not a scraper defect, so callers treat it
// specially: the run is recorded as a success with 0 rows (so any open
// claude-fix issue auto-closes) rather than failed, and no new issue is filed.
//
// It must only be thrown once the scraper has positively confirmed the source
// is healthy — never as a catch-all for an empty result, which is usually a
// broken selector and should still surface as a failure.
public sealed class SourceEmptyException : Exception
{
    public SourceEmptyException(string message) : base(message) { }
}
