using System.Globalization;
using BabyBrain.Scrapers.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BabyBrain.Web.Data;

public class BabyBrainDbContext : DbContext
{
    public BabyBrainDbContext(DbContextOptions<BabyBrainDbContext> options) : base(options) { }

    public DbSet<EventOccurrence> EventOccurrences => Set<EventOccurrence>();
    public DbSet<Geocode> Geocodes => Set<Geocode>();
    public DbSet<ScrapeRun> ScrapeRuns => Set<ScrapeRun>();

    // ISO-8601 with offset, single space separator — byte-identical to what EF
    // Core was already writing for DateTimeOffset on SQLite by default. Adding
    // an explicit converter lets the LINQ translator see a string-typed column
    // (so ORDER BY / WHERE BETWEEN translate), while existing rows stay valid.
    // Lexical ordering of this format matches chronological ordering.
    private const string DateTimeOffsetFormat = "yyyy-MM-dd HH:mm:ss.fffffffzzz";

    private static readonly ValueConverter<DateTimeOffset, string> DateTimeOffsetConverter =
        new(dto => dto.ToString(DateTimeOffsetFormat, CultureInfo.InvariantCulture),
            s => DateTimeOffset.ParseExact(s, DateTimeOffsetFormat, CultureInfo.InvariantCulture));

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<Geocode>().HasKey(g => g.Postcode);
        mb.Entity<Geocode>().Property(g => g.ResolvedAt).HasConversion(DateTimeOffsetConverter);

        var e = mb.Entity<EventOccurrence>();
        e.HasIndex(x => x.ExternalKey).IsUnique();
        e.HasIndex(x => new { x.Date, x.StartTime });
        e.HasIndex(x => x.Source);
        e.HasIndex(x => x.Category);
        e.Property(x => x.LastSeenAt).HasConversion(DateTimeOffsetConverter);
        e.Property(x => x.Date).HasConversion(
            d => d.ToString("yyyy-MM-dd"),
            s => DateOnly.ParseExact(s, "yyyy-MM-dd"));
        e.Property(x => x.StartTime).HasConversion(
            t => t.ToString("HH:mm"),
            s => TimeOnly.ParseExact(s, "HH:mm"));
        e.Property(x => x.EndTime).HasConversion(
            t => t == null ? null : t.Value.ToString("HH:mm"),
            s => s == null ? null : TimeOnly.ParseExact(s, "HH:mm"));

        var sr = mb.Entity<ScrapeRun>();
        // Fetching last 5 runs per source is the dominant access pattern.
        sr.HasIndex(x => new { x.Source, x.StartedAt });
        sr.Property(x => x.StartedAt).HasConversion(DateTimeOffsetConverter);
        sr.Property(x => x.CompletedAt).HasConversion(DateTimeOffsetConverter);
    }
}
