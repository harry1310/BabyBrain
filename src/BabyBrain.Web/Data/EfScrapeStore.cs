using BabyBrain.Scrapers.Domain;

namespace BabyBrain.Web.Data;

public sealed class EfScrapeStore : IScrapeStore
{
    private readonly BabyBrainDbContext _db;

    public EfScrapeStore(BabyBrainDbContext db) => _db = db;

    public Task UpsertOccurrencesAsync(string sourceId, IReadOnlyList<EventOccurrence> rows, CancellationToken ct = default)
        => EventOccurrenceUpsert.ApplyAsync(_db, sourceId, rows, ct);

    public async Task RecordRunAsync(ScrapeRun run, CancellationToken ct = default)
    {
        _db.ScrapeRuns.Add(run);
        await _db.SaveChangesAsync(ct);
    }
}
