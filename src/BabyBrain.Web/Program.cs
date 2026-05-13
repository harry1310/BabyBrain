using BabyBrain.Scrapers;
using BabyBrain.Scrapers.Barbican;
using BabyBrain.Scrapers.BritishMuseum;
using BabyBrain.Scrapers.Camden;
using BabyBrain.Scrapers.Islington;
using BabyBrain.Scrapers.Shared;
using BabyBrain.Scrapers.Southbank;
using BabyBrain.Scrapers.Tockify;
using BabyBrain.Scrapers.Va;
using BabyBrain.Scrapers.WigmoreHall;
using BabyBrain.Web.Data;
using BabyBrain.Web.Middleware;
using BabyBrain.Web.Services;
using BabyBrain.Web.Services.SelfHealing;
using Anthropic;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var razor = builder.Services.AddRazorPages();
if (builder.Environment.IsDevelopment())
{
    razor.AddRazorRuntimeCompilation();
}

// SQLite path: BABYBRAIN_DB_PATH wins (set in the container to /data/babybrain.db),
// otherwise fall back to the local App_Data folder for dev runs.
var dbPath = builder.Configuration["BABYBRAIN_DB_PATH"];
if (string.IsNullOrWhiteSpace(dbPath))
{
    dbPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "babybrain.db");
}
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
builder.Services.AddDbContext<BabyBrainDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));

// Persist Data Protection keys to the mounted volume so antiforgery tokens,
// cookies, etc. survive container rebuilds. Without this each rebuild
// generates fresh keys, breaking every existing browser session.
var dpKeyDir = Path.Combine(Path.GetDirectoryName(dbPath)!, "dataprotection-keys");
Directory.CreateDirectory(dpKeyDir);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dpKeyDir))
    .SetApplicationName("BabyBrain");

builder.Services.AddHttpClient();
// Typed HttpClient for TfL — 8s timeout so a slow upstream doesn't hold the request open.
builder.Services.AddHttpClient<TflJourneyService>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(8);
    c.DefaultRequestHeaders.Add("User-Agent", "BabyBrain/1.0 (+https://github.com/harry1310/BabyBrain)");
});
builder.Services.AddSingleton<IScrapeStatusTracker, ScrapeStatusTracker>();
builder.Services.AddScoped<GeocodingService>();
builder.Services.AddScoped<IScrapeStore, EfScrapeStore>();
builder.Services.AddScoped<ScrapeRunner>();

// Alerting: when BABYBRAIN_GH_TOKEN + BABYBRAIN_GH_REPO are set (e.g.
// "harry1310/BabyBrain"), failing scrapes open a GitHub issue and recovering
// scrapes close it. Otherwise alerting is a no-op so dev/local runs don't
// need the token.
//
// Self-healing layers on top: when ANTHROPIC_API_KEY is also set AND a fresh
// issue is opened, Claude is asked for a diagnosis + patch and a draft PR is
// opened that closes the issue when merged.
const string GhClientName = "github-alerts";
var ghToken = builder.Configuration["BABYBRAIN_GH_TOKEN"];
var ghRepo = builder.Configuration["BABYBRAIN_GH_REPO"];
var anthropicKey = builder.Configuration["ANTHROPIC_API_KEY"];
var claudeModel = builder.Configuration["BABYBRAIN_CLAUDE_MODEL"] ?? "claude-opus-4-7";

if (!string.IsNullOrWhiteSpace(ghToken) && !string.IsNullOrWhiteSpace(ghRepo) && ghRepo.Contains('/'))
{
    var parts = ghRepo.Split('/', 2);
    var owner = parts[0];
    var repo = parts[1];
    builder.Services.AddHttpClient(GhClientName, c =>
    {
        // 60s here (was 10s) so the heal pipeline's slower calls — branch
        // create, contents PUT, PR open — have headroom over a flaky link.
        c.BaseAddress = new Uri("https://api.github.com/");
        c.Timeout = TimeSpan.FromSeconds(60);
        c.DefaultRequestHeaders.UserAgent.ParseAdd("BabyBrain-ScrapeAlerts/1.0");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ghToken);
        c.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    });

    if (!string.IsNullOrWhiteSpace(anthropicKey))
    {
        var capturedKey = anthropicKey;
        builder.Services.AddSingleton(_ => new AnthropicClient { ApiKey = capturedKey });
        builder.Services.AddScoped<IClaudeHealer>(sp => new ClaudeHealer(
            sp.GetRequiredService<AnthropicClient>(),
            claudeModel,
            sp.GetRequiredService<ILogger<ClaudeHealer>>()));
        builder.Services.AddScoped(sp => new GitHubSourceFetcher(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(GhClientName),
            owner, repo, sp.GetRequiredService<ILogger<GitHubSourceFetcher>>()));
        builder.Services.AddScoped(sp => new GitHubPatchPublisher(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(GhClientName),
            owner, repo, sp.GetRequiredService<ILogger<GitHubPatchPublisher>>()));
        builder.Services.AddScoped<SelfHealingOrchestrator>();
    }
    else
    {
        builder.Services.AddScoped<IClaudeHealer, NoopClaudeHealer>();
    }

    builder.Services.AddScoped<IScrapeAlertSink>(sp => new GitHubScrapeAlertSink(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient(GhClientName),
        owner, repo,
        // Resolve orchestrator only when Anthropic is configured — registered conditionally above.
        !string.IsNullOrWhiteSpace(anthropicKey) ? sp.GetRequiredService<SelfHealingOrchestrator>() : null,
        sp.GetRequiredService<ILogger<GitHubScrapeAlertSink>>()));
}
else
{
    builder.Services.AddSingleton<IScrapeAlertSink, NoopScrapeAlertSink>();
}

// HTML archive: enabled only when BABYBRAIN_HTML_ARCHIVE_PATH is set. Saved
// pages are useful for debugging failed scrapes and for any future programmatic
// remediation flow. Production default in docker-compose is /data/scrape-html.
var htmlArchivePath = builder.Configuration["BABYBRAIN_HTML_ARCHIVE_PATH"];
if (!string.IsNullOrWhiteSpace(htmlArchivePath))
{
    var capturedPath = htmlArchivePath;
    builder.Services.AddSingleton<IHtmlArchive>(sp =>
        new FileHtmlArchive(capturedPath, sp.GetRequiredService<ILogger<FileHtmlArchive>>()));
}
else
{
    builder.Services.AddSingleton<IHtmlArchive>(NullHtmlArchive.Instance);
}

builder.Services.AddSingleton<PlaywrightFetcher>();
builder.Services.AddScoped<IScraper, CamdenStayAndPlayScraper>();
builder.Services.AddScoped<IScraper, FitzroviaTockifyScraper>();
builder.Services.AddScoped<IScraper, IslingtonFindYourScraper>();
builder.Services.AddScoped<IScraper, BritishMuseumScraper>();
builder.Services.AddScoped<IScraper, SouthbankCentreScraper>();
builder.Services.AddScoped<IScraper, VaEarlyYearsScraper>();

// Wigmore needs a browser-shaped UA (CloudFront 403s on the bare default).
// 30s timeout because we follow each listing item to its detail page for price.
builder.Services.AddHttpClient<WigmoreHallUnderFivesScraper>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (compatible; BabyBrainScraper/1.0; +https://github.com/harry1310/BabyBrain)");
});
builder.Services.AddScoped<IScraper>(sp => sp.GetRequiredService<WigmoreHallUnderFivesScraper>());

// Barbican is also server-rendered HTML behind CF — same UA pattern, follows
// each event's detail page for the standard ticket price.
builder.Services.AddHttpClient<BarbicanParentAndBabyScraper>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (compatible; BabyBrainScraper/1.0; +https://github.com/harry1310/BabyBrain)");
});
builder.Services.AddScoped<IScraper>(sp => sp.GetRequiredService<BarbicanParentAndBabyScraper>());

builder.Services.AddHostedService<DailyScrapeService>();

var app = builder.Build();

// Auto-apply migrations on startup so first-run is just `dotnet run`.
// Also resolves any postcodes missing a geocode — fast and idempotent.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BabyBrainDbContext>();
    db.Database.Migrate();
    var geocoder = scope.ServiceProvider.GetRequiredService<GeocodingService>();
    try { await geocoder.ResolveMissingAsync(); }
    catch (Exception ex) { app.Logger.LogError(ex, "Startup geocode pass failed"); }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

if (app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseRouting();

app.UseWhen(
    ctx => ctx.Request.Path.StartsWithSegments("/Admin", StringComparison.OrdinalIgnoreCase),
    branch => branch.UseMiddleware<BasicAuthMiddleware>());

app.UseAuthorization();
app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();

// Step-free journey lookup via TfL Unified API. Called from the map popup
// in Index.cshtml when the user clicks "Step-free →". `from` accepts either
// a "lat,lng" pair (browser geolocation) or a postcode (manual fallback when
// geolocation isn't available — e.g. HTTP origins where browsers deny it).
app.MapGet("/api/step-free-journey", async (
    string from, string to,
    TflJourneyService svc, CancellationToken ct) =>
{
    var result = await svc.GetStepFreeJourneyAsync(from, to, ct);
    return Results.Json(result);
});

// Async scrape endpoints — under /Admin so they inherit the basic auth gate.
// Each POST queues a fire-and-forget Task.Run and returns immediately so the
// browser doesn't sit blocked while a scrape grinds; the Admin UI polls
// /Admin/api/source-status to know when to refresh.
app.MapPost("/Admin/api/rerun-source", (
    RerunSourceRequest req,
    IScrapeStatusTracker tracker,
    IServiceProvider services,
    ILogger<Program> logger) =>
{
    if (string.IsNullOrWhiteSpace(req.Source))
        return Results.BadRequest(new { error = "source required" });

    if (!tracker.TryStart(req.Source))
        return Results.Json(new { queued = false, message = "Already running" });

    _ = Task.Run(async () =>
    {
        try
        {
            using var scope = services.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<ScrapeRunner>();
            await runner.RunByIdAsync(req.Source);
        }
        catch (Exception ex) { logger.LogError(ex, "Background rerun failed for {Source}", req.Source); }
        finally { tracker.Finish(req.Source); }
    });

    return Results.Json(new { queued = true });
});

app.MapPost("/Admin/api/rerun-all", (
    IEnumerable<IScraper> scrapers,
    IScrapeStatusTracker tracker,
    IServiceProvider services,
    ILogger<Program> logger) =>
{
    var queued = new List<string>();
    foreach (var s in scrapers)
        if (tracker.TryStart(s.SourceId)) queued.Add(s.SourceId);

    if (queued.Count == 0)
        return Results.Json(new { queued = 0, message = "All sources already running" });

    _ = Task.Run(async () =>
    {
        using var scope = services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<ScrapeRunner>();
        foreach (var src in queued)
        {
            try { await runner.RunByIdAsync(src); }
            catch (Exception ex) { logger.LogError(ex, "Background rerun-all failed for {Source}", src); }
            finally { tracker.Finish(src); }
        }
    });

    return Results.Json(new { queued = queued.Count });
});

app.MapGet("/Admin/api/source-status", (IScrapeStatusTracker tracker) =>
    Results.Json(new { running = tracker.RunningSources }));

// Public "report a mistake" endpoint — fired by the dialog on every event
// card. Idempotent; re-reporting just updates the timestamp.
app.MapPost("/api/report-event", async (
    ReportEventRequest req,
    BabyBrainDbContext db,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.ExternalKey))
        return Results.BadRequest(new { error = "externalKey required" });

    var row = await db.EventOccurrences.FirstOrDefaultAsync(e => e.ExternalKey == req.ExternalKey, ct);
    if (row is null) return Results.NotFound();

    row.ReportedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
    return Results.Json(new { reported = true });
});

// Admin-only — clears the report flag once the issue is dealt with.
app.MapPost("/Admin/api/mark-fixed", async (
    ReportEventRequest req,
    BabyBrainDbContext db,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.ExternalKey))
        return Results.BadRequest(new { error = "externalKey required" });

    var row = await db.EventOccurrences.FirstOrDefaultAsync(e => e.ExternalKey == req.ExternalKey, ct);
    if (row is null) return Results.NotFound();

    row.ReportedAt = null;
    await db.SaveChangesAsync(ct);
    return Results.Json(new { cleared = true });
});

// Public — accept a user-suggested new source URL. Validation is intentionally
// lenient: must parse as an absolute http(s) URL, capped to the column's
// 500-char limit. Duplicates are allowed; Admin can dedupe by eye when reviewing.
app.MapPost("/api/suggest-source", async (
    SuggestSourceRequest req,
    BabyBrainDbContext db,
    CancellationToken ct) =>
{
    var raw = req.Url?.Trim() ?? "";
    if (raw.Length == 0 || raw.Length > 500)
        return Results.BadRequest(new { error = "URL must be 1-500 characters" });
    if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
        (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
    {
        return Results.BadRequest(new { error = "Must be a full http(s) URL" });
    }

    db.SourceSuggestions.Add(new SourceSuggestion
    {
        Url = uri.ToString(),
        SubmittedAt = DateTimeOffset.UtcNow,
    });
    await db.SaveChangesAsync(ct);
    return Results.Json(new { submitted = true });
});

// Admin-only — clear a suggestion's pending state (kept for history; not deleted).
app.MapPost("/Admin/api/mark-suggestion-reviewed", async (
    MarkSuggestionRequest req,
    BabyBrainDbContext db,
    CancellationToken ct) =>
{
    var row = await db.SourceSuggestions.FirstOrDefaultAsync(s => s.Id == req.Id, ct);
    if (row is null) return Results.NotFound();
    row.ReviewedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
    return Results.Json(new { reviewed = true });
});

app.Run();

public sealed record RerunSourceRequest(string Source);
public sealed record ReportEventRequest(string ExternalKey);
public sealed record SuggestSourceRequest(string? Url);
public sealed record MarkSuggestionRequest(int Id);
