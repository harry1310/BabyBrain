namespace BabyBrain.Web.Services;

public sealed class DailyScrapeService : BackgroundService
{
    private static readonly TimeOnly RunAt = new(3, 0);

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
            var runner = scope.ServiceProvider.GetRequiredService<ScrapeRunner>();
            var result = await runner.RunAllAsync(ct: ct);
            _logger.LogInformation("Scheduled scrape complete. {Summary}", result.Summary);
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
