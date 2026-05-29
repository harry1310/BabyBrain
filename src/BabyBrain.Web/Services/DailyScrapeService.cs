using BabyBrain.Scrapers;
using BabyBrain.Scrapers.Shared;

namespace BabyBrain.Web.Services;

// Wakes once a day at RunAt UTC and runs every registered scraper in source-id
// order. There used to be a per-source override mechanism so the British
// Museum could fire later than everyone else, but it was only ever used to
// dodge its Cloudflare flakiness — once BM moved to ScraperAPI (#22, #23) the
// override was no longer needed, so the whole multi-slot scheduler is gone.
public sealed class DailyScrapeService : BackgroundService
{
    private static readonly TimeOnly RunAt = new(3, 0);
    private const int HtmlArchiveKeepPerUrl = 5;

    private readonly IServiceProvider _services;
    private readonly ILogger<DailyScrapeService> _logger;
    private readonly bool _scrapeOnStartup;
    private readonly IReadOnlyList<string> _sourceIds;

    public DailyScrapeService(IServiceProvider services, ILogger<DailyScrapeService> logger, IConfiguration config)
    {
        _services = services;
        _logger = logger;
        _scrapeOnStartup = string.Equals(
            config["BABYBRAIN_SCRAPE_ON_STARTUP"],
            "true",
            StringComparison.OrdinalIgnoreCase);

        // Enumerate scrapers once at startup so the schedule is fixed for the
        // lifetime of the host. Scrapers are scoped, so we need a scope.
        using var scope = services.CreateScope();
        _sourceIds = scope.ServiceProvider.GetServices<IScraper>()
            .Select(s => s.SourceId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _logger.LogInformation("Scrape slot {Time:HH\\:mm} UTC: {Sources}",
            RunAt, string.Join(", ", _sourceIds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_scrapeOnStartup)
        {
            _logger.LogInformation("BABYBRAIN_SCRAPE_ON_STARTUP=true, running immediate scrape of {Count} sources", _sourceIds.Count);
            await RunGroupAsync(_sourceIds, stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeUntilNextRun(DateTime.UtcNow);
            _logger.LogInformation("Next scrape slot at {Time:HH\\:mm} UTC in {Delay} ({Count} sources)",
                RunAt, delay, _sourceIds.Count);
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { return; }

            await RunGroupAsync(_sourceIds, stoppingToken);
        }
    }

    internal static TimeSpan TimeUntilNextRun(DateTime utcNow)
    {
        var todayRun = DateTime.SpecifyKind(utcNow.Date.Add(RunAt.ToTimeSpan()), DateTimeKind.Utc);
        var next = utcNow < todayRun ? todayRun : todayRun.AddDays(1);
        return next - utcNow;
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
