namespace BabyBrain.Scrapers.Domain;

// A single dated instance of a baby/toddler event. The search page queries this
// table directly. Recurring weekly sessions are materialised into many rows
// covering the search horizon (e.g. next 90 days); dated events (e.g. from a
// Tockify calendar) are stored as one row each.
public class EventOccurrence
{
    public int Id { get; set; }

    // Stable identity from the source so re-scrapes update rather than duplicate.
    // For recurring sources: "{source}:{venueKey}:{sessionKey}:{yyyy-MM-dd}".
    // For dated sources: the source's own event ID + occurrence timestamp.
    public required string ExternalKey { get; set; }

    public required string Source { get; set; }            // "camden_stay_and_play", etc.
    public required string Category { get; set; }          // see Categories constants
    public string? SourceUrl { get; set; }

    public required DateOnly Date { get; set; }
    public required TimeOnly StartTime { get; set; }
    public TimeOnly? EndTime { get; set; }

    public required string SessionName { get; set; }
    public string? SessionNotes { get; set; }

    public required string VenueName { get; set; }
    public string? VenueAddress { get; set; }
    public string? Postcode { get; set; }

    public int? MinAgeMonths { get; set; }
    public int? MaxAgeMonths { get; set; }

    public bool TermTimeOnly { get; set; }

    // Cost in £. IsFree is the explicit "free" marker (scraper found "Free" text);
    // Cost is the amount in pounds when scraped (typically the lowest ticket tier
    // for sources with multiple price bands). Both null/false means unknown.
    public bool IsFree { get; set; }
    public decimal? Cost { get; set; }

    // Set on every upsert so we can prune stale rows the source no longer lists.
    public DateTimeOffset LastSeenAt { get; set; }
}
