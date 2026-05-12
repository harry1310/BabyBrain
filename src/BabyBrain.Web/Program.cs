using BabyBrain.Scrapers;
using BabyBrain.Scrapers.BritishMuseum;
using BabyBrain.Scrapers.Camden;
using BabyBrain.Scrapers.Islington;
using BabyBrain.Scrapers.Shared;
using BabyBrain.Scrapers.Southbank;
using BabyBrain.Scrapers.Tockify;
using BabyBrain.Scrapers.Va;
using BabyBrain.Web.Data;
using BabyBrain.Web.Middleware;
using BabyBrain.Web.Services;
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
builder.Services.AddScoped<GeocodingService>();
builder.Services.AddScoped<IScrapeStore, EfScrapeStore>();
builder.Services.AddScoped<ScrapeRunner>();

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

app.Run();
