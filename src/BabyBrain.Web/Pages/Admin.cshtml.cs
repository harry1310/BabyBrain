using BabyBrain.Scrapers;
using BabyBrain.Web.Data;
using BabyBrain.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BabyBrain.Web.Pages;

public class AdminModel : PageModel
{
    private readonly BabyBrainDbContext _db;
    private readonly ScrapeRunner _runner;
    private readonly ILogger<AdminModel> _logger;

    public AdminModel(BabyBrainDbContext db, ScrapeRunner runner, ILogger<AdminModel> logger)
    {
        _db = db;
        _runner = runner;
        _logger = logger;
    }

    public string? LastRunSummary { get; private set; }
    public string? LastRunError { get; private set; }
    public int TotalEvents { get; private set; }
    public int DistinctSources { get; private set; }
    public DateOnly? EarliestDate { get; private set; }
    public DateOnly? LatestDate { get; private set; }

    public async Task OnGetAsync() => await LoadStatsAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        try
        {
            var result = await _runner.RunAllAsync();
            LastRunSummary = result.Summary;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scraper run failed");
            LastRunError = ex.Message;
        }
        await LoadStatsAsync();
        return Page();
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
}
