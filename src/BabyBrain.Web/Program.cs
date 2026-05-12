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

builder.Services.AddHttpClient();
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

app.Run();
