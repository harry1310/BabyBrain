using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BabyBrain.Web.Services.SelfHealing;

// Creates a branch from main, commits each heal patch to it via the Contents
// API (one commit per file — squashable on merge), then opens a draft PR that
// closes the alert issue when merged. Idempotent: returns null without acting
// if an open auto-heal PR for the same source already exists.
//
// Requires the configured PAT to have Contents:write and Pull requests:write
// in addition to the Issues:write the alert sink uses.
public sealed class GitHubPatchPublisher
{
    private const string LabelName = "auto-heal";
    private const string LabelColor = "0e8a16"; // GitHub's "good first issue" green
    private const string LabelDescription = "PR generated automatically by the self-healing pipeline.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _owner;
    private readonly string _repo;
    private readonly ILogger<GitHubPatchPublisher> _logger;

    public GitHubPatchPublisher(HttpClient http, string owner, string repo, ILogger<GitHubPatchPublisher> logger)
    {
        _http = http;
        _owner = owner;
        _repo = repo;
        _logger = logger;
    }

    // Returns the PR number on success, null when nothing was opened (existing
    // open PR, empty patch set, or any failure on the GitHub side — all logged).
    public async Task<int?> OpenDraftPrAsync(string sourceId, int issueNumber, HealResult heal, CancellationToken ct = default)
    {
        if (heal.Patches.Count == 0)
        {
            _logger.LogInformation("No patches to publish for {Source}", sourceId);
            return null;
        }

        try
        {
            await EnsureLabelAsync(ct);

            var title = TitleFor(sourceId);
            if (await OpenPrExistsAsync(title, ct))
            {
                _logger.LogInformation("Skipping heal PR for {Source} — open PR already exists", sourceId);
                return null;
            }

            var baseSha = await GetMainHeadShaAsync(ct);
            if (baseSha is null) return null;

            var branch = $"auto-heal/{sourceId}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            if (!await CreateBranchAsync(branch, baseSha, ct)) return null;

            foreach (var patch in heal.Patches)
            {
                if (!await PutFileOnBranchAsync(branch, patch, sourceId, ct))
                {
                    _logger.LogWarning("Patch commit failed for {Path}; abandoning heal PR for {Source}", patch.Path, sourceId);
                    return null;
                }
            }

            var prNumber = await OpenPullRequestAsync(title, branch, issueNumber, heal, ct);
            if (prNumber is null) return null;

            await AddLabelToPrAsync(prNumber.Value, ct);
            _logger.LogInformation("Opened heal PR #{Pr} for {Source} closing #{Issue}", prNumber, sourceId, issueNumber);
            return prNumber;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Heal PR publish failed for {Source}", sourceId);
            return null;
        }
    }

    private static string TitleFor(string sourceId) => $"Auto-heal: {sourceId}";

    private async Task<bool> OpenPrExistsAsync(string title, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(
            $"/repos/{_owner}/{_repo}/pulls?state=open&head={_owner}:auto-heal&per_page=100", ct);
        resp.EnsureSuccessStatusCode();
        var prs = await resp.Content.ReadFromJsonAsync<List<PullRef>>(Json, ct) ?? new();
        return prs.Any(p => p.Title == title);
    }

    private async Task<string?> GetMainHeadShaAsync(CancellationToken ct)
    {
        using var resp = await _http.GetAsync($"/repos/{_owner}/{_repo}/git/ref/heads/main", ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<RefResponse>(Json, ct);
        return body?.Object?.Sha;
    }

    private async Task<bool> CreateBranchAsync(string branch, string baseSha, CancellationToken ct)
    {
        var payload = new { @ref = $"refs/heads/{branch}", sha = baseSha };
        using var resp = await _http.PostAsJsonAsync($"/repos/{_owner}/{_repo}/git/refs", payload, Json, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Branch create failed ({Status}): {Body}", resp.StatusCode, body);
            return false;
        }
        return true;
    }

    private async Task<bool> PutFileOnBranchAsync(string branch, HealPatch patch, string sourceId, CancellationToken ct)
    {
        // PUT /contents requires the existing blob SHA when updating. Fetch
        // it from the branch head (which, on the first patch, equals main).
        string? existingSha = null;
        using (var getResp = await _http.GetAsync($"/repos/{_owner}/{_repo}/contents/{patch.Path}?ref={branch}", ct))
        {
            if (getResp.IsSuccessStatusCode)
            {
                var meta = await getResp.Content.ReadFromJsonAsync<ContentMeta>(Json, ct);
                existingSha = meta?.Sha;
            }
            else if (getResp.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                getResp.EnsureSuccessStatusCode();
            }
        }

        var contentB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(patch.NewContent));
        var payload = new
        {
            message = $"Auto-heal {sourceId}: update {patch.Path}",
            content = contentB64,
            branch,
            sha = existingSha,
        };
        using var resp = await _http.PutAsJsonAsync($"/repos/{_owner}/{_repo}/contents/{patch.Path}", payload, Json, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("File commit failed ({Status}) for {Path}: {Body}", resp.StatusCode, patch.Path, body);
            return false;
        }
        return true;
    }

    private async Task<int?> OpenPullRequestAsync(string title, string branch, int issueNumber, HealResult heal, CancellationToken ct)
    {
        var body = $$"""
        Automated proposal generated by the BabyBrain self-healing pipeline. Closes #{{issueNumber}}.

        **Claude's diagnosis:**

        > {{heal.Diagnosis}}

        Files changed: {{heal.Patches.Count}}. Review carefully before merging — this PR was machine-generated and may be wrong.
        """;
        var payload = new
        {
            title,
            head = branch,
            @base = "main",
            body,
            draft = true,
        };
        using var resp = await _http.PostAsJsonAsync($"/repos/{_owner}/{_repo}/pulls", payload, Json, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("PR open failed ({Status}): {Body}", resp.StatusCode, err);
            return null;
        }
        var pr = await resp.Content.ReadFromJsonAsync<PullRef>(Json, ct);
        return pr?.Number;
    }

    private async Task AddLabelToPrAsync(int prNumber, CancellationToken ct)
    {
        // PRs use the issues labels endpoint (same numeric namespace).
        var payload = new { labels = new[] { LabelName } };
        using var resp = await _http.PostAsJsonAsync($"/repos/{_owner}/{_repo}/issues/{prNumber}/labels", payload, Json, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogDebug("Label attach failed ({Status}): {Body}", resp.StatusCode, body);
        }
    }

    private async Task EnsureLabelAsync(CancellationToken ct)
    {
        using var check = await _http.GetAsync($"/repos/{_owner}/{_repo}/labels/{LabelName}", ct);
        if (check.IsSuccessStatusCode) return;
        if (check.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            check.EnsureSuccessStatusCode();
        }
        var payload = new { name = LabelName, color = LabelColor, description = LabelDescription };
        using var create = await _http.PostAsJsonAsync($"/repos/{_owner}/{_repo}/labels", payload, Json, ct);
        if (!create.IsSuccessStatusCode && create.StatusCode != System.Net.HttpStatusCode.UnprocessableEntity)
        {
            create.EnsureSuccessStatusCode();
        }
    }

    private sealed class RefResponse
    {
        [JsonPropertyName("object")] public RefObject? Object { get; set; }
    }
    private sealed class RefObject
    {
        [JsonPropertyName("sha")] public string Sha { get; set; } = "";
    }
    private sealed class ContentMeta
    {
        [JsonPropertyName("sha")] public string? Sha { get; set; }
    }
    private sealed class PullRef
    {
        [JsonPropertyName("number")] public int Number { get; set; }
        [JsonPropertyName("title")] public string Title { get; set; } = "";
    }
}
