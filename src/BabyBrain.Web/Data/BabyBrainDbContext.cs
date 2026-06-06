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
    public DbSet<SourceSuggestion> SourceSuggestions => Set<SourceSuggestion>();
    public DbSet<ResolvedAgeCacheEntry> ResolvedAgeCache => Set<ResolvedAgeCacheEntry>();

    // ISO-8601 with offset, single space separator. Adding an explicit
    // converter lets the LINQ translator see a string-typed column (so
    // ORDER BY / WHERE BETWEEN translate). Lexical ordering of this format
    // matches chronological ordering.
    //
    // Write side: always 7 fractional digits (zero-padded) for consistency.
    // Read side: ParseExact with multiple accepted formats — old rows
    // written before this converter sometimes have 6 fractional digits,
    // or none at all (when the time landed on an exact second).
    private const string DateTimeOffsetWriteFormat = "yyyy-MM-dd HH:mm:ss.fffffffzzz";

    private static readonly string[] DateTimeOffsetReadFormats =
    {
        "yyyy-MM-dd HH:mm:ss.FFFFFFFzzz",   // 0–7 fractional digits, space separator (covers all space-sep variants)
        "yyyy-MM-ddTHH:mm:ss.FFFFFFFzzz",   // T separator (in case any row uses ISO-8601 strict)
    };

    private static readonly ValueConverter<DateTimeOffset, string> DateTimeOffsetConverter =
        new(dto => dto.ToString(DateTimeOffsetWriteFormat, CultureInfo.InvariantCulture),
            s => DateTimeOffset.ParseExact(s, DateTimeOffsetReadFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal));

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
        // Nullable variant — same wire format, just null-tolerant on both ends.
        e.Property(x => x.ReportedAt).HasConversion(
            dto => dto == null ? null : dto.Value.ToString(DateTimeOffsetWriteFormat, CultureInfo.InvariantCulture),
            s => s == null ? null : DateTimeOffset.ParseExact(s, DateTimeOffsetReadFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal));
        e.HasIndex(x => x.ReportedAt);
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

        var ss = mb.Entity<SourceSuggestion>();
        // Admin's "pending" list filters by ReviewedAt is null then sorts by SubmittedAt desc.
        ss.HasIndex(x => x.ReviewedAt);
        ss.Property(x => x.Url).HasMaxLength(500);
        ss.Property(x => x.SubmittedAt).HasConversion(DateTimeOffsetConverter);
        ss.Property(x => x.ReviewedAt).HasConversion(
            dto => dto == null ? null : dto.Value.ToString(DateTimeOffsetWriteFormat, CultureInfo.InvariantCulture),
            s => s == null ? null : DateTimeOffset.ParseExact(s, DateTimeOffsetReadFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal));

        var ac = mb.Entity<ResolvedAgeCacheEntry>();
        // One probe per event; lookups are always by (Source, SourceUrl).
        ac.HasIndex(x => new { x.Source, x.SourceUrl }).IsUnique();
        ac.Property(x => x.SourceUrl).HasMaxLength(500);
        ac.Property(x => x.CheckedAt).HasConversion(DateTimeOffsetConverter);
    }
}
