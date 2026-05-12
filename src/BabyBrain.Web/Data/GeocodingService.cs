using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace BabyBrain.Web.Data;

// Resolves UK postcodes to lat/lng via postcodes.io (free, no auth, generous
// limits). Bulk endpoint accepts up to 100 postcodes per request, which keeps
// us well clear of rate limits even when seeding the cache.
public sealed class GeocodingService
{
    private const string BulkUrl = "https://api.postcodes.io/postcodes";
    private const int BatchSize = 100;

    private readonly IHttpClientFactory _http;
    private readonly BabyBrainDbContext _db;
    private readonly ILogger<GeocodingService> _logger;

    public GeocodingService(IHttpClientFactory http, BabyBrainDbContext db, ILogger<GeocodingService> logger)
    {
        _http = http;
        _db = db;
        _logger = logger;
    }

    public async Task<int> ResolveMissingAsync(CancellationToken ct = default)
    {
        var db = _db;
        var allPostcodes = await db.EventOccurrences
            .Where(e => e.Postcode != null)
            .Select(e => e.Postcode!)
            .Distinct()
            .ToListAsync(ct);

        var normalised = allPostcodes
            .Select(p => Geocode.Normalise(p))
            .Where(p => p.Length > 0)
            .Distinct()
            .ToList();

        var known = await db.Geocodes.Select(g => g.Postcode).ToListAsync(ct);
        var todo = normalised.Except(known).ToList();
        if (todo.Count == 0) return 0;

        _logger.LogInformation("Geocoding {Count} new postcodes via postcodes.io", todo.Count);
        var client = _http.CreateClient();
        var resolved = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var batch in Chunk(todo, BatchSize))
        {
            var body = JsonSerializer.Serialize(new { postcodes = batch });
            using var req = new HttpRequestMessage(HttpMethod.Post, BulkUrl)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("postcodes.io returned {Status}", resp.StatusCode);
                continue;
            }

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("result", out var resultsEl)) continue;
            foreach (var item in resultsEl.EnumerateArray())
            {
                if (!item.TryGetProperty("query", out var q)) continue;
                if (!item.TryGetProperty("result", out var r) || r.ValueKind == JsonValueKind.Null) continue;
                if (!r.TryGetProperty("latitude", out var latEl) || latEl.ValueKind == JsonValueKind.Null) continue;
                if (!r.TryGetProperty("longitude", out var lngEl) || lngEl.ValueKind == JsonValueKind.Null) continue;

                db.Geocodes.Add(new Geocode
                {
                    Postcode = Geocode.Normalise(q.GetString() ?? ""),
                    Latitude = latEl.GetDouble(),
                    Longitude = lngEl.GetDouble(),
                    ResolvedAt = now,
                });
                resolved++;
            }
            await db.SaveChangesAsync(ct);
        }
        return resolved;
    }

    private static IEnumerable<List<T>> Chunk<T>(IList<T> items, int size)
    {
        for (var i = 0; i < items.Count; i += size)
            yield return items.Skip(i).Take(size).ToList();
    }
}
