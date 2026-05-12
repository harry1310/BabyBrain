using BabyBrain.Scrapers.Shared;

namespace BabyBrain.Web.Services;

public sealed class DailyScrapeService : BackgroundService
{
    private static readonly TimeOnly RunAt = new(3, 0);
    private const int HtmlArchiveKeepPerUrl = 5;

    private readonly IServiceProvider _services;
    private readonly ILogger<DailyScrapeService> _logger;
    private readonly bool _scrapeOnStartup;

    public DailyScrapeService(IServiceProvider services, ILogger<DailyScrapeService> logger, IConfiguration config)
    {
        _services = services;
        _logger = logger;
        _scrapeOnStartup = string.Equals(
            config["BABYBRAIN_SCRAPE_ON_STARTUP"],
            "true",
            StringComparison.OrdinalIgnoreCase);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_scrapeOnStartup)
        {
            _logger.LogInformation("BABYBRAIN_SCRAPE_ON_STARTUP=true, running immediate scrape");
            await RunOnceAsync(stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeUntilNextRun(DateTime.UtcNow);
            _logger.LogInformation("Next scheduled scrape in {Delay} (at {When:yyyy-MM-dd HH:mm} UTC)",
                delay, DateTime.UtcNow.Add(delay));
            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) { return; }

            await RunOnceAsync(stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();

            // Prune yesterday's archived HTML before the new run so we don't
            // accumulate disk forever. Failure here mustn't stop the scrape.
            try
            {
                var archive = scope.ServiceProvider.GetRequiredService<IHtmlArchive>();
                await archive.PruneAsync(HtmlArchiveKeepPerUrl, ct);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "HTML archive prune failed"); }

            var runner = scope.ServiceProvider.GetRequiredService<ScrapeRunner>();
            var result = await runner.RunAllAsync(ct: ct);
            var perSource = string.Join("; ", result.Outcomes.Select(o =>
                o.Success ? $"{o.Source}: {o.Rows}" : $"{o.Source}: FAILED"));
            _logger.LogInformation(
                "Scheduled scrape complete. {Summary}. Geocoded {Geo} new postcodes.",
                perSource, result.NewGeocodes);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled scrape failed");
        }
    }

    internal static TimeSpan TimeUntilNextRun(DateTime utcNow)
    {
        var todayRun = new DateTime(utcNow.Year, utcNow.Month, utcNow.Day, RunAt.Hour, RunAt.Minute, 0, DateTimeKind.Utc);
        var next = utcNow < todayRun ? todayRun : todayRun.AddDays(1);
        return next - utcNow;
    }
}
