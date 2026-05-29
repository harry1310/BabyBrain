using BabyBrain.Scrapers;
using BabyBrain.Scrapers.BachToBaby;
using BabyBrain.Scrapers.Barbican;
using BabyBrain.Scrapers.Better;
using BabyBrain.Scrapers.BritishMuseum;
using BabyBrain.Scrapers.Camden;
using BabyBrain.Scrapers.CityOfLondon;
using BabyBrain.Scrapers.DesignMuseum;
using BabyBrain.Scrapers.Holborn;
using BabyBrain.Scrapers.Islington;
using BabyBrain.Scrapers.Lso;
using BabyBrain.Scrapers.Ltm;
using BabyBrain.Scrapers.MwHealth;
using BabyBrain.Scrapers.PostalMuseum;
using BabyBrain.Scrapers.Shared;
using BabyBrain.Scrapers.Southbank;
using BabyBrain.Scrapers.Tockify;
using BabyBrain.Scrapers.Va;
using BabyBrain.Scrapers.WigmoreHall;
using BabyBrain.Scrapers.WildLondon;
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
builder.Services.AddSingleton<UkBankHolidayService>();
builder.Services.AddHostedService<UkBankHolidayRefreshService>();
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

    // Shared GitHub Issues client — used by the alert sink, the Admin
    // "raise issue" action, and the API-fallback service.
    builder.Services.AddScoped(sp => new GitHubIssueClient(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient(GhClientName), owner, repo));

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

        // 6-hour API fallback: heals `claude-fix` issues that no Claude Code
        // console claimed in time. Only runs when the API key is present.
        builder.Services.AddHostedService<IssueFallbackService>();
    }
    else
    {
        builder.Services.AddScoped<IClaudeHealer, NoopClaudeHealer>();
    }

    // The sink only raises/closes issues now — healing is deferred to either
    // an open console or IssueFallbackService.
    builder.Services.AddScoped<IScrapeAlertSink, GitHubScrapeAlertSink>();
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
// ScrapingApiFetcher proxies BM + Southbank fetches through ScraperAPI's
// residential-proxy endpoint. Both sites' Cloudflare blocks the Hetzner VPS
// outright (even curl from prod hit a "Just a moment..." challenge), so we
// need someone with a clean residential IP to do the fetch for us. Key
// comes from BABYBRAIN_SCRAPERAPI_KEY in the container env.
var scraperApiKey = builder.Configuration["BABYBRAIN_SCRAPERAPI_KEY"];
if (string.IsNullOrWhiteSpace(scraperApiKey))
{
    throw new InvalidOperationException(
        "BABYBRAIN_SCRAPERAPI_KEY is not set — required for the British Museum and " +
        "Southbank Centre scrapers. Get a free key at https://www.scraperapi.com/.");
}
const string scraperApiClient = "scraperapi";
builder.Services.AddHttpClient(scraperApiClient, c =>
{
    // ScraperAPI can take a while to solve a Cloudflare challenge — give it
    // headroom on a per-call basis, then the orchestrator's overall scrape
    // timeout still bounds the worst case.
    c.Timeout = TimeSpan.FromSeconds(90);
});
builder.Services.AddSingleton(sp => new ScrapingApiFetcher(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(scraperApiClient),
    scraperApiKey!,
    sp.GetRequiredService<ILogger<ScrapingApiFetcher>>()));
builder.Services.AddScoped<IScraper, CamdenStayAndPlayScraper>();
builder.Services.AddScoped<IScraper, FitzroviaTockifyScraper>();
builder.Services.AddScoped<IScraper, IslingtonFindYourScraper>();
builder.Services.AddScoped<IScraper, BritishMuseumScraper>();
builder.Services.AddScoped<IScraper, SouthbankCentreScraper>();
builder.Services.AddScoped<IScraper, WildLondonFamilyLearningScraper>();
// V&A: Playwright fetches the listing, but when a listing card has no
// itemprop="description" we fall back to a plain HttpClient GET of the
// detail page just for its <meta name="description"> — much faster than
// rendering a second time with Playwright.
builder.Services.AddHttpClient<VaEarlyYearsScraper>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
    c.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (compatible; BabyBrainScraper/1.0; +https://github.com/harry1310/BabyBrain)");
});
builder.Services.AddScoped<IScraper>(sp => sp.GetRequiredService<VaEarlyYearsScraper>());

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

// Design Museum: server-rendered HTML on a vanilla CMS, fine over plain HTTP.
builder.Services.AddHttpClient<DesignMuseumFamiliesScraper>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (compatible; BabyBrainScraper/1.0; +https://github.com/harry1310/BabyBrain)");
});
builder.Services.AddScoped<IScraper>(sp => sp.GetRequiredService<DesignMuseumFamiliesScraper>());

// Postal Museum: single recurring "Post and Play" event page. Behind
// Cloudflare, so it goes through Playwright rather than a plain HttpClient.
builder.Services.AddScoped<IScraper, PostalMuseumPostAndPlayScraper>();

// Talacre soft play via Better's (GLL) booking API — plain JSON, but the API
// 404s unless the request carries an Origin header for the booking sub-domain.
builder.Services.AddHttpClient<TalacreSoftPlayScraper>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    c.DefaultRequestHeaders.Add("Origin", "https://bookings.better.org.uk");
    c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
});
builder.Services.AddScoped<IScraper>(sp => sp.GetRequiredService<TalacreSoftPlayScraper>());

// City of London libraries: server-rendered HTML behind a CDN that 403s any
// non-mainstream UA (the BabyBrainScraper UA included), so this one needs a
// full Chrome UA string — verified returning 200.
builder.Services.AddHttpClient<CityOfLondonLibrariesScraper>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-GB,en;q=0.9");
});
builder.Services.AddScoped<IScraper>(sp => sp.GetRequiredService<CityOfLondonLibrariesScraper>());

// LSO under-5s "Musical Storytelling" concerts: server-rendered WordPress HTML,
// fine over plain HTTP with a browser-shaped UA. Follows each concert's detail
// page for the child ticket price.
builder.Services.AddHttpClient<LsoUnder5sConcertsScraper>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-GB,en;q=0.9");
});
builder.Services.AddScoped<IScraper>(sp => sp.GetRequiredService<LsoUnder5sConcertsScraper>());

// London Transport Museum "Singing and Story" under-5s sessions: Drupal HTML
// behind Cloudflare, so it goes through Playwright. Fetches a second page (the
// "Show all dates" target) for the full term-time-aware date list.
builder.Services.AddScoped<IScraper, LtmSingingAndStoryScraper>();

// Holborn Community Association early-years activities. The HCA page embeds a
// Plinth booking calendar; we read events from Plinth's Next.js JSON. Plain
// HTTP with a browser-shaped UA is fine here.
builder.Services.AddHttpClient<HolbornEarlyYearsScraper>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-GB,en;q=0.9");
});
builder.Services.AddScoped<IScraper>(sp => sp.GetRequiredService<HolbornEarlyYearsScraper>());

// Moon Women's Health baby/child/family classes: a server-rendered HTML table,
// fine over plain HTTP with a browser-shaped UA.
builder.Services.AddHttpClient<MwHealthClassesScraper>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-GB,en;q=0.9");
});
builder.Services.AddScoped<IScraper>(sp => sp.GetRequiredService<MwHealthClassesScraper>());

// Bach to Baby classical concerts for babies/families. The site's WAF
// fingerprints and blocks .NET's HTTP client, so this scraper goes through
// Playwright (a real browser) like the Cloudflare-fronted sources.
builder.Services.AddScoped<IScraper, BachToBabyConcertsScraper>();

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
// card. Idempotent; re-reporting just updates the timestamp + field. The
// reportedField is required and must be one of the known tokens.
app.MapPost("/api/report-event", async (
    ReportEventRequest req,
    BabyBrainDbContext db,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.ExternalKey))
        return Results.BadRequest(new { error = "externalKey required" });
    if (!ReportedFields.IsValid(req.ReportedField))
        return Results.BadRequest(new { error = "reportedField must be one of the known tokens" });

    var row = await db.EventOccurrences.FirstOrDefaultAsync(e => e.ExternalKey == req.ExternalKey, ct);
    if (row is null) return Results.NotFound();

    row.ReportedAt = DateTimeOffset.UtcNow;
    row.ReportedField = req.ReportedField;
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
    row.ReportedField = null;
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

// Admin-only — raise a GitHub issue about a reported event. The reviewer's
// free-text description plus the event's own details go into the issue body;
// it lands in the same `claude-fix` queue (and identical downstream flow) as
// scrape-failure issues.
app.MapPost("/Admin/api/raise-issue", async (
    RaiseIssueRequest req,
    BabyBrainDbContext db,
    IServiceProvider sp,
    CancellationToken ct) =>
{
    var github = sp.GetService<GitHubIssueClient>();
    if (github is null)
        return Results.Json(new { error = "GitHub is not configured on this server." }, statusCode: 503);

    if (string.IsNullOrWhiteSpace(req.ExternalKey))
        return Results.BadRequest(new { error = "externalKey required" });
    var description = req.Description?.Trim() ?? "";
    if (description.Length == 0 || description.Length > 4000)
        return Results.BadRequest(new { error = "Description must be 1-4000 characters" });

    var row = await db.EventOccurrences.FirstOrDefaultAsync(e => e.ExternalKey == req.ExternalKey, ct);
    if (row is null) return Results.NotFound();

    var title = $"Reported event: {(row.SessionName.Length <= 80 ? row.SessionName : row.SessionName[..80])}";
    var venue = row.VenueName + (string.IsNullOrEmpty(row.Postcode) ? "" : $" ({row.Postcode})");
    var body = $$"""
        A reviewer flagged a BabyBrain event via the Admin screen.

        **Reviewer's description**

        {{description}}

        **Reported event**

        - Title: {{row.SessionName}}
        - Venue: {{venue}}
        - Date / time: {{row.Date:yyyy-MM-dd}} {{row.StartTime:HH\:mm}}
        - Source: `{{row.Source}}`
        - Reported field: {{ReportedFields.Label(row.ReportedField)}}
        - Source URL: {{(string.IsNullOrEmpty(row.SourceUrl) ? "(none)" : row.SourceUrl)}}
        - ExternalKey: `{{row.ExternalKey}}`

        Queued for a Claude Code fix — an open console picks it up, otherwise the API fallback runs after 6 hours.

        {{IssueConventions.SourceMarker(row.Source)}}
        """;

    try
    {
        await IssueConventions.EnsureLabelsAsync(github, ct);
        var created = await github.CreateIssueAsync(
            title, body,
            new[] { IssueConventions.ReportedMistake, IssueConventions.ClaudeFix },
            ct);
        return Results.Json(new { number = created?.Number, url = created?.HtmlUrl });
    }
    catch (Exception)
    {
        return Results.Json(new { error = "Failed to raise the issue on GitHub." }, statusCode: 502);
    }
});

app.Run();

public sealed record RerunSourceRequest(string Source);
public sealed record ReportEventRequest(string ExternalKey, string? ReportedField);
public sealed record SuggestSourceRequest(string? Url);
public sealed record MarkSuggestionRequest(int Id);
public sealed record RaiseIssueRequest(string ExternalKey, string? Description);
