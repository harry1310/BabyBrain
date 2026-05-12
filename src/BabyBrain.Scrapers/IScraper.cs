using BabyBrain.Scrapers.Domain;

namespace BabyBrain.Scrapers;

public interface IScraper
{
    string SourceId { get; }

    // Default/primary category for this source. See Categories for allowed
    // values. Individual rows may override this — e.g. a council directory
    // listing both libraries and children's centres can stamp rows with the
    // best-fitting category per service.
    string Category { get; }

    // Materialise event occurrences covering [today, today + horizonDays).
    // The scraper is responsible for expanding any weekly recurrence into
    // concrete dated rows within that window.
    Task<IReadOnlyList<EventOccurrence>> ScrapeAsync(int horizonDays, CancellationToken ct = default);
}
