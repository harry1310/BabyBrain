using System.Globalization;
using BabyBrain.Scrapers;
using BabyBrain.Scrapers.Shared;

namespace BabyBrain.Web.Services;

// Wakes once per scheduled slot, runs the sources due at that slot. By default
// every scraper fires together at DefaultRunAt UTC. Individual sources can be
// moved to a different slot via env vars of the form
//   BABYBRAIN_SCRAPE_OVERRIDES__<source_id>=HH:mm   (UTC, 24h)
// e.g. BABYBRAIN_SCRAPE_OVERRIDES__british_museum_family=06:00
// Sources sharing a time run together as a group, in source-id order.
public sealed class DailyScrapeService : BackgroundService
{
    private static readonly TimeOnly DefaultRunAt = new(6, 30);
    private const string OverridesSection = "BABYBRAIN_SCRAPE_OVERRIDES";
    private const int HtmlArchiveKeepPerUrl = 5;

    private readonly IServiceProvider _services;
    private readonly ILogger<DailyScrapeService> _logger;
    private readonly bool _scrapeOnStartup;
    private readonly IReadOnlyDictionary<TimeOnly, IReadOnlyList<string>> _schedule;

    public DailyScrapeService(IServiceProvider services, ILogger<DailyScrapeService> logger, IConfiguration config)
    {
        _services = services;
        _logger = logger;
        _scrapeOnStartup = string.Equals(
            config["BABYBRAIN_SCRAPE_ON_STARTUP"],
            "true",
            StringComparison.OrdinalIgnoreCase);

        // Scraper SourceIds are stable constants — enumerate once at startup so
        // the schedule is fixed for the lifetime of the host. Scrapers are
        // scoped, so we need a scope to resolve them.
        using var scope = services.CreateScope();
        var sourceIds = scope.ServiceProvider.GetServices<IScraper>()
            .Select(s => s.SourceId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _schedule = BuildSchedule(config, sourceIds, logger);

        foreach (var (time, ids) in _schedule.OrderBy(kv => kv.Key))
        {
            _logger.LogInformation("Scrape slot {Time:HH\\:mm} UTC: {Sources}", time, string.Join(", ", ids));
        }
    }

    private static IReadOnlyDictionary<TimeOnly, IReadOnlyList<string>> BuildSchedule(
        IConfiguration config, IReadOnlyList<string> sourceIds, ILogger logger)
    {
        // Pull every BABYBRAIN_SCRAPE_OVERRIDES__<source_id>=HH:mm pair. Unknown
        // source ids and unparseable times are warned and ignored — wrong env
        // var must never bring the scheduler down.
        var overrides = new Dictionary<string, TimeOnly>(StringComparer.OrdinalIgnoreCase);
        var known = new HashSet<string>(sourceIds, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in config.GetSection(OverridesSection).GetChildren())
        {
            if (string.IsNullOrWhiteSpace(entry.Value)) continue;
            if (!TimeOnly.TryParseExact(entry.Value, new[] { "HH:mm", "H:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            {
                logger.LogWarning("Scrape override for {Source} has unparseable time {Value} (expected HH:mm) — ignored", entry.Key, entry.Value);
                continue;
            }
            if (!known.Contains(entry.Key))
            {
                logger.LogWarning("Scrape override for unknown source {Source}={Time} — ignored. Registered sources: {Known}",
                    entry.Key, t, string.Join(", ", sourceIds));
                continue;
            }
            overrides[entry.Key] = t;
        }

        var groups = new Dictionary<TimeOnly, List<string>>();
        foreach (var id in sourceIds)
        {
            var when = overrides.TryGetValue(id, out var t) ? t : DefaultRunAt;
            if (!groups.TryGetValue(when, out var group))
            {
                group = new List<string>();
                groups[when] = group;
            }
            group.Add(id);
        }
        foreach (var key in groups.Keys.ToList())
        {
            groups[key].Sort(StringComparer.OrdinalIgnoreCase);
        }
        return groups.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_scrapeOnStartup)
        {
            var allSources = _schedule.Values.SelectMany(v => v).ToList();
            _logger.LogInformation("BABYBRAIN_SCRAPE_ON_STARTUP=true, running immediate scrape of {Count} sources", allSources.Count);
            await RunGroupAsync(allSources, stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var (when, sources, delay) = NextDue(DateTime.UtcNow);
            _logger.LogInformation("Next scrape slot at {Time:HH\\:mm} UTC in {Delay} ({Count} sources)",
                when, delay, sources.Count);
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { return; }

            await RunGroupAsync(sources, stoppingToken);
        }
    }

    internal (TimeOnly When, IReadOnlyList<string> Sources, TimeSpan Delay) NextDue(DateTime utcNow)
    {
        var today = utcNow.Date;
        var best = _schedule
            .Select(kv =>
            {
                var todayRun = DateTime.SpecifyKind(today.Add(kv.Key.ToTimeSpan()), DateTimeKind.Utc);
                var next = utcNow < todayRun ? todayRun : todayRun.AddDays(1);
                return (When: kv.Key, Sources: kv.Value, Delay: next - utcNow);
            })
            .OrderBy(x => x.Delay)
            .First();
        return best;
    }

    private async Task RunGroupAsync(IReadOnlyList<string> sourceIds, CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();

            try
            {
                var archive = scope.ServiceProvider.GetRequiredService<IHtmlArchive>();
                await archive.PruneAsync(HtmlArchiveKeepPerUrl, ct);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "HTML archive prune failed"); }

            var runner = scope.ServiceProvider.GetRequiredService<ScrapeRunner>();
            var summary = new List<string>();
            foreach (var id in sourceIds)
            {
                ct.ThrowIfCancellationRequested();
                var result = await runner.RunByIdAsync(id, ct: ct);
                if (result is null)
                {
                    summary.Add($"{id}: UNKNOWN");
                    continue;
                }
                var outcome = result.Outcomes.Single();
                summary.Add(outcome.Success ? $"{outcome.Source}: {outcome.Rows}" : $"{outcome.Source}: FAILED");
            }
            _logger.LogInformation("Scrape slot complete. {Summary}", string.Join("; ", summary));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scrape slot failed");
        }
    }
}
