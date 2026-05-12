using System.Text.Json;

namespace BabyBrain.Web.Services;

// Calls TfL's Unified API for step-free journey planning. The public
// Journey Planner web UI doesn't accept accessibility params via URL, but
// the JSON API at api.tfl.gov.uk does — `accessibilityPreference=StepFreeToVehicle`
// gives us the route we want.
//
// Anonymous use is fine at our volume (TfL throttles per IP, not per app key).
// Add `app_key` if we ever push above ~500 requests/min.
public sealed class TflJourneyService
{
    private const string BaseUrl = "https://api.tfl.gov.uk";

    private readonly HttpClient _http;
    private readonly ILogger<TflJourneyService> _logger;

    public TflJourneyService(HttpClient http, ILogger<TflJourneyService> logger)
    {
        _http = http;
        _logger = logger;
    }

    // TfL's Journey endpoint accepts either a "lat,lng" pair or a postcode for
    // both `from` and `to`, so we take strings here and let callers pass either.
    public async Task<JourneyResponse> GetStepFreeJourneyAsync(
        string from, string to, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(from)) return JourneyResponse.Failed("Origin required.");
        if (string.IsNullOrWhiteSpace(to))   return JourneyResponse.Failed("Destination required.");

        var fromEsc = Uri.EscapeDataString(from.Trim());
        var toEsc   = Uri.EscapeDataString(to.Trim());
        var url = $"{BaseUrl}/Journey/JourneyResults/{fromEsc}/to/{toEsc}?accessibilityPreference=StepFreeToVehicle";

        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("TfL journey returned {Status} for {Url}", resp.StatusCode, url);
                return JourneyResponse.Failed("TfL Journey Planner is currently unavailable.");
            }
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return Parse(doc.RootElement);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return JourneyResponse.Failed("TfL request timed out.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "TfL journey HTTP error");
            return JourneyResponse.Failed("Could not reach TfL Journey Planner.");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "TfL journey JSON parse error");
            return JourneyResponse.Failed("TfL response could not be parsed.");
        }
    }

    private static JourneyResponse Parse(JsonElement root)
    {
        if (!root.TryGetProperty("journeys", out var journeysEl) ||
            journeysEl.ValueKind != JsonValueKind.Array ||
            journeysEl.GetArrayLength() == 0)
        {
            return JourneyResponse.Failed("No step-free route found for this trip.");
        }

        var options = new List<JourneyOption>();
        foreach (var j in journeysEl.EnumerateArray())
        {
            var duration = TryGetInt(j, "duration");
            var fare = TryGetFare(j);
            var legs = new List<JourneyLeg>();

            if (j.TryGetProperty("legs", out var legsEl) && legsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var l in legsEl.EnumerateArray())
                {
                    legs.Add(new JourneyLeg(
                        Mode: TryGetModeName(l) ?? "unknown",
                        DurationMinutes: TryGetInt(l, "duration") ?? 0,
                        LineName: TryGetLineName(l),
                        FromName: TryGetPointName(l, "departurePoint"),
                        ToName: TryGetPointName(l, "arrivalPoint")));
                }
            }

            options.Add(new JourneyOption(
                DurationMinutes: duration ?? legs.Sum(x => x.DurationMinutes),
                Fare: fare,
                Legs: legs));

            if (options.Count >= 3) break; // top 3 only
        }

        return new JourneyResponse(Success: true, Error: null, Journeys: options);
    }

    private static int? TryGetInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : null;

    private static string? TryGetFare(JsonElement journey)
    {
        if (!journey.TryGetProperty("fare", out var fareEl) || fareEl.ValueKind != JsonValueKind.Object) return null;
        if (!fareEl.TryGetProperty("totalCost", out var costEl) || !costEl.TryGetInt32(out var pence)) return null;
        return $"£{pence / 100.0:F2}";
    }

    private static string? TryGetModeName(JsonElement leg)
    {
        if (!leg.TryGetProperty("mode", out var modeEl) || modeEl.ValueKind != JsonValueKind.Object) return null;
        if (!modeEl.TryGetProperty("name", out var nameEl)) return null;
        return nameEl.GetString();
    }

    private static string? TryGetLineName(JsonElement leg)
    {
        if (!leg.TryGetProperty("routeOptions", out var routesEl) || routesEl.ValueKind != JsonValueKind.Array) return null;
        foreach (var r in routesEl.EnumerateArray())
        {
            if (r.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
            {
                var s = n.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }
        return null;
    }

    private static string? TryGetPointName(JsonElement leg, string field)
    {
        if (!leg.TryGetProperty(field, out var pt) || pt.ValueKind != JsonValueKind.Object) return null;
        if (!pt.TryGetProperty("commonName", out var n)) return null;
        return n.GetString();
    }
}

public sealed record JourneyResponse(bool Success, string? Error, IReadOnlyList<JourneyOption>? Journeys)
{
    public static JourneyResponse Failed(string error) => new(false, error, null);
}

public sealed record JourneyOption(int DurationMinutes, string? Fare, IReadOnlyList<JourneyLeg> Legs);

public sealed record JourneyLeg(string Mode, int DurationMinutes, string? LineName, string? FromName, string? ToName);
