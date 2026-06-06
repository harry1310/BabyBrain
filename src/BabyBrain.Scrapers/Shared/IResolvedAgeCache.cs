namespace BabyBrain.Scrapers.Shared;

// A small persistent cache of ages resolved from a source's per-event detail
// page, so a daily scrape doesn't re-fetch an unchanged event's age every run.
// Keyed by (source, sourceUrl).
//
// Motivation: Southbank's detail fetches go through ScraperAPI, where that
// premium-proxied domain costs ~10 credits per request — and an event's age
// guidance doesn't change day to day. Probing once and reusing the result for
// a TTL turns "fetch every event every run" into "fetch each event ~once a
// month", the bulk of the source's daily credit spend.
//
// A stored entry with both ages null means "we probed the detail page and it
// carried no age guidance" — deliberately distinct from a cache miss, so
// age-less events are also fetched only once per TTL rather than every run.
public interface IResolvedAgeCache
{
    // The cached probe if one exists and was taken within maxAge; null on a
    // miss or when the entry is staler than maxAge (caller should re-probe).
    Task<AgeProbe?> TryGetAsync(string source, string sourceUrl, TimeSpan maxAge, CancellationToken ct = default);

    // Record the outcome of a detail-page probe. min/max may both be null
    // ("checked, no age guidance found"); that is cached just like a hit.
    Task SetAsync(string source, string sourceUrl, int? minAgeMonths, int? maxAgeMonths, CancellationToken ct = default);
}

// A previously-resolved age band. Both null = probed but no age guidance found.
public readonly record struct AgeProbe(int? MinAgeMonths, int? MaxAgeMonths);
