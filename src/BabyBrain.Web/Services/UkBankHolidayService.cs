using System.Text.Json;

namespace BabyBrain.Web.Services;

// In-memory cache of UK (England & Wales) bank holiday dates, refreshed weekly
// from gov.uk's official feed. Used by the search pages to suppress sources
// that are reliably closed on bank holidays (libraries, council community
// centres) — those scrapers publish a regular weekly slot but the venue
// itself doesn't run on the holiday, so the slot would be misleading.
//
// If the feed is unreachable the set stays as it was (empty on first boot,
// last successful refresh thereafter). An empty set degrades to a no-op
// filter — we'd rather show stale events than wrongly hide them.
public sealed class UkBankHolidayService
{
    private volatile HashSet<DateOnly> _holidays = new();

    public bool IsBankHoliday(DateOnly date) => _holidays.Contains(date);

    public IReadOnlySet<DateOnly> Holidays => _holidays;

    internal void Update(IEnumerable<DateOnly> dates)
    {
        _holidays = new HashSet<DateOnly>(dates);
    }
}

public sealed class UkBankHolidayRefreshService : BackgroundService
{
    private const string FeedUrl = "https://www.gov.uk/bank-holidays.json";
    private const string Division = "england-and-wales";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(7);

    private readonly UkBankHolidayService _store;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<UkBankHolidayRefreshService> _logger;

    public UkBankHolidayRefreshService(
        UkBankHolidayService store,
        IHttpClientFactory http,
        ILogger<UkBankHolidayRefreshService> logger)
    {
        _store = store;
        _http = http;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await RefreshOnceAsync(ct);
            try { await Task.Delay(RefreshInterval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RefreshOnceAsync(CancellationToken ct)
    {
        try
        {
            using var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("BabyBrain/1.0 (+https://github.com/harry1310/BabyBrain)");

            await using var stream = await client.GetStreamAsync(FeedUrl, ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var dates = new HashSet<DateOnly>();
            if (doc.RootElement.TryGetProperty(Division, out var division)
                && division.TryGetProperty("events", out var events)
                && events.ValueKind == JsonValueKind.Array)
            {
                foreach (var ev in events.EnumerateArray())
                {
                    if (ev.TryGetProperty("date", out var d)
                        && d.ValueKind == JsonValueKind.String
                        && DateOnly.TryParse(d.GetString(), out var parsed))
                    {
                        dates.Add(parsed);
                    }
                }
            }

            if (dates.Count == 0)
            {
                _logger.LogWarning("gov.uk bank holiday feed parsed to zero dates — keeping previous set ({Count})", _store.Holidays.Count);
                return;
            }

            _store.Update(dates);
            _logger.LogInformation("Refreshed UK bank holidays ({Division}): {Count} dates", Division, dates.Count);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh UK bank holidays from gov.uk — keeping previous set ({Count})", _store.Holidays.Count);
        }
    }
}
