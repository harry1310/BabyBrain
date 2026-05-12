using BabyBrain.Scrapers.Domain;
using Microsoft.EntityFrameworkCore;

namespace BabyBrain.Web.Data;

public class BabyBrainDbContext : DbContext
{
    public BabyBrainDbContext(DbContextOptions<BabyBrainDbContext> options) : base(options) { }

    public DbSet<EventOccurrence> EventOccurrences => Set<EventOccurrence>();
    public DbSet<Geocode> Geocodes => Set<Geocode>();
    public DbSet<ScrapeRun> ScrapeRuns => Set<ScrapeRun>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<Geocode>().HasKey(g => g.Postcode);

        var e = mb.Entity<EventOccurrence>();
        e.HasIndex(x => x.ExternalKey).IsUnique();
        e.HasIndex(x => new { x.Date, x.StartTime });
        e.HasIndex(x => x.Source);
        e.HasIndex(x => x.Category);

        var sr = mb.Entity<ScrapeRun>();
        // Fetching last 5 runs per source is the dominant access pattern.
        sr.HasIndex(x => new { x.Source, x.StartedAt });
        e.Property(x => x.Date).HasConversion(
            d => d.ToString("yyyy-MM-dd"),
            s => DateOnly.ParseExact(s, "yyyy-MM-dd"));
        e.Property(x => x.StartTime).HasConversion(
            t => t.ToString("HH:mm"),
            s => TimeOnly.ParseExact(s, "HH:mm"));
        e.Property(x => x.EndTime).HasConversion(
            t => t == null ? null : t.Value.ToString("HH:mm"),
            s => s == null ? null : TimeOnly.ParseExact(s, "HH:mm"));
    }
}
