using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;

namespace BabyBrain.Web.Services.SelfHealing;

// Reads a single source file from the configured GitHub repo's default branch.
// Used by the heal pipeline because the running container only has compiled
// binaries on disk — Claude needs the actual .cs files to propose changes.
public sealed class GitHubSourceFetcher
{
    private readonly HttpClient _http;
    private readonly string _owner;
    private readonly string _repo;
    private readonly ILogger<GitHubSourceFetcher> _logger;

    public GitHubSourceFetcher(HttpClient http, string owner, string repo, ILogger<GitHubSourceFetcher> logger)
    {
        _http = http;
        _owner = owner;
        _repo = repo;
        _logger = logger;
    }

    public async Task<HealPatch?> FetchAsync(string path, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync($"/repos/{_owner}/{_repo}/contents/{path}", ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadFromJsonAsync<ContentResponse>(cancellationToken: ct);
            if (body?.Content is null) return null;
            // GitHub returns base64 with embedded newlines — strip them before decoding.
            var clean = body.Content.Replace("\n", "").Replace("\r", "");
            var bytes = Convert.FromBase64String(clean);
            return new HealPatch(path, Encoding.UTF8.GetString(bytes));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch GitHub source file {Path}", path);
            return null;
        }
    }

    private sealed class ContentResponse
    {
        [JsonPropertyName("content")] public string? Content { get; set; }
    }
}
