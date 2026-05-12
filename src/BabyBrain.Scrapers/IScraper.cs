using BabyBrain.Scrapers.Domain;

namespace BabyBrain.Scrapers;

public interface IScraper
{
    string SourceId { get; }

    // Category every row from this scraper gets stamped with. See Categories
    // for the allowed values. A single scraper covers a single category in v1
    // — split into two scrapers if a source spans categories.
    string Category { get; }

    // Materialise event occurrences covering [today, today + horizonDays).
    // The scraper is responsible for expanding any weekly recurrence into
    // concrete dated rows within that window.
    Task<IReadOnlyList<EventOccurrence>> ScrapeAsync(int horizonDays, CancellationToken ct = default);
}
