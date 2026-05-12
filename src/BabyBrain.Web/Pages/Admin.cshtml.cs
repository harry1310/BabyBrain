using BabyBrain.Scrapers;
using BabyBrain.Web.Data;
using BabyBrain.Web.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BabyBrain.Web.Pages;

public class AdminModel : PageModel
{
    private const int HistoryWindow = 5;

    private readonly BabyBrainDbContext _db;
    private readonly IEnumerable<IScraper> _scrapers;
    private readonly IScrapeStatusTracker _statusTracker;

    public AdminModel(BabyBrainDbContext db, IEnumerable<IScraper> scrapers, IScrapeStatusTracker statusTracker)
    {
        _db = db;
        _scrapers = scrapers;
        _statusTracker = statusTracker;
    }

    public int TotalEvents { get; private set; }
    public int DistinctSources { get; private set; }
    public DateOnly? EarliestDate { get; private set; }
    public DateOnly? LatestDate { get; private set; }
    public IReadOnlyList<SourceHistory> History { get; private set; } = Array.Empty<SourceHistory>();
    public IReadOnlyCollection<string> RunningSources { get; private set; } = Array.Empty<string>();

    public async Task OnGetAsync()
    {
        await LoadStatsAsync();
        await LoadHistoryAsync();
        RunningSources = _statusTracker.RunningSources;
    }

    private async Task LoadStatsAsync()
    {
        TotalEvents = await _db.EventOccurrences.CountAsync();
        DistinctSources = await _db.EventOccurrences.Select(e => e.Source).Distinct().CountAsync();
        if (TotalEvents > 0)
        {
            EarliestDate = await _db.EventOccurrences.MinAsync(e => e.Date);
            LatestDate = await _db.EventOccurrences.MaxAsync(e => e.Date);
        }
    }

    private async Task LoadHistoryAsync()
    {
        var list = new List<SourceHistory>();
        foreach (var scraper in _scrapers)
        {
            var recent = await _db.ScrapeRuns
                .Where(r => r.Source == scraper.SourceId)
                .OrderByDescending(r => r.StartedAt)
                .Take(HistoryWindow)
                .ToListAsync();
            list.Add(new SourceHistory(scraper.SourceId, recent));
        }
        History = list;
    }

    // A successful run is "in red" when its row count is at most 50% of the
    // most recent earlier successful run's count. Indexes into the recent list
    // run newest-first.
    public static bool IsDrop(IReadOnlyList<ScrapeRun> recent, int index)
    {
        var current = recent[index];
        if (current.Status != ScrapeRun.StatusSuccess) return false;
        for (var i = index + 1; i < recent.Count; i++)
        {
            if (recent[i].Status != ScrapeRun.StatusSuccess) continue;
            return recent[i].RowsScraped > 0 && current.RowsScraped * 2 <= recent[i].RowsScraped;
        }
        return false;
    }

    public sealed record SourceHistory(string Source, IReadOnlyList<ScrapeRun> Recent);
}
