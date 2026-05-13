namespace BabyBrain.Web.Data;

// User-submitted URL suggesting a new event source for BabyBrain to scrape.
// Submitted via the public footer dialog; surfaced in Admin so we can decide
// whether to write a scraper for it. Marked reviewed (not deleted) so the
// Admin can see prior decisions without re-evaluating duplicates.
public class SourceSuggestion
{
    public int Id { get; set; }

    public required string Url { get; set; }

    public DateTimeOffset SubmittedAt { get; set; }

    // Null = still pending. Set by Admin "Mark reviewed" once handled.
    public DateTimeOffset? ReviewedAt { get; set; }
}
