using BabyBrain.Scrapers.Domain;
using Microsoft.EntityFrameworkCore;

namespace BabyBrain.Web.Data;

public static class EventOccurrenceUpsert
{
    // Upsert by ExternalKey, then prune any rows from this source that were not
    // seen in this run (they've been removed upstream).
    public static async Task ApplyAsync(BabyBrainDbContext db, string sourceId, IReadOnlyList<EventOccurrence> incoming, CancellationToken ct = default)
    {
        var runAt = DateTimeOffset.UtcNow;
        var keys = incoming.Select(e => e.ExternalKey).ToHashSet();
        var existing = await db.EventOccurrences
            .Where(e => e.Source == sourceId)
            .ToDictionaryAsync(e => e.ExternalKey, ct);

        foreach (var row in incoming)
        {
            if (existing.TryGetValue(row.ExternalKey, out var current))
            {
                current.Category = row.Category;
                current.SourceUrl = row.SourceUrl;
                current.Date = row.Date;
                current.StartTime = row.StartTime;
                current.EndTime = row.EndTime;
                current.TimeApproximate = row.TimeApproximate;
                current.SessionName = row.SessionName;
                current.SessionNotes = row.SessionNotes;
                current.VenueName = row.VenueName;
                current.VenueAddress = row.VenueAddress;
                current.Postcode = row.Postcode;
                current.MinAgeMonths = row.MinAgeMonths;
                current.MaxAgeMonths = row.MaxAgeMonths;
                current.TermTimeOnly = row.TermTimeOnly;
                current.IsFree = row.IsFree;
                current.Cost = row.Cost;
                current.LastSeenAt = runAt;
            }
            else
            {
                row.LastSeenAt = runAt;
                db.EventOccurrences.Add(row);
            }
        }

        var stale = existing.Values.Where(e => !keys.Contains(e.ExternalKey));
        db.EventOccurrences.RemoveRange(stale);

        await db.SaveChangesAsync(ct);
    }
}
