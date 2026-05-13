using BabyBrain.Scrapers.Domain;
using Microsoft.EntityFrameworkCore;

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

    public async Task<IReadOnlyList<ScrapeRun>> GetRecentRunsAsync(string sourceId, int take, CancellationToken ct = default)
    {
        return await _db.ScrapeRuns
            .Where(r => r.Source == sourceId)
            .OrderByDescending(r => r.StartedAt)
            .Take(take)
            .ToListAsync(ct);
    }
}
