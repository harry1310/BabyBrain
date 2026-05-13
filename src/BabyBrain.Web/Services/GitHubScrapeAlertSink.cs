using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BabyBrain.Web.Services;

// Opens a GitHub Issue when a scraper has failed two runs in a row, and closes
// it on the next successful run. Idempotent: one open issue per source at most,
// keyed by the `scrape-failure` label plus a stable title. The label is
// auto-created on first use so the repo doesn't need any manual prep.
//
// Failure modes (no network, bad token, missing repo) are caught and logged —
// alerting must never break the scrape.
public sealed class GitHubScrapeAlertSink : IScrapeAlertSink
{
    private const int OpenIssueThreshold = 2; // failures-in-a-row before alerting
    private const string Label = "scrape-failure";
    private const string LabelColor = "d73a4a"; // GitHub's "bug" red
    private const string LabelDescription = "An automated scraper has failed multiple runs in a row.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _owner;
    private readonly string _repo;
    private readonly ILogger<GitHubScrapeAlertSink> _logger;

    public GitHubScrapeAlertSink(HttpClient http, string owner, string repo, ILogger<GitHubScrapeAlertSink> logger)
    {
        _http = http;
        _owner = owner;
        _repo = repo;
        _logger = logger;
    }

    public async Task OnFailureAsync(string sourceId, string error, int consecutiveFailures, CancellationToken ct = default)
    {
        if (consecutiveFailures < OpenIssueThreshold) return;
        try
        {
            await EnsureLabelAsync(ct);
            if (await FindOpenIssueAsync(sourceId, ct) is not null) return; // already alerted
            await CreateIssueAsync(sourceId, error, consecutiveFailures, ct);
            _logger.LogInformation("Opened GitHub issue for failing scraper {Source}", sourceId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GitHub alert (failure) failed for {Source}", sourceId);
        }
    }

    public async Task OnRecoveryAsync(string sourceId, CancellationToken ct = default)
    {
        try
        {
            if (await FindOpenIssueAsync(sourceId, ct) is not { } number) return;
            await PostCommentAsync(number, "Recovered — next scrape succeeded. Closing.", ct);
            await CloseIssueAsync(number, ct);
            _logger.LogInformation("Closed GitHub issue #{Number} for recovered scraper {Source}", number, sourceId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GitHub alert (recovery) failed for {Source}", sourceId);
        }
    }

    private static string TitleFor(string sourceId) => $"Scrape failing: {sourceId}";

    private async Task<int?> FindOpenIssueAsync(string sourceId, CancellationToken ct)
    {
        var url = $"/repos/{_owner}/{_repo}/issues?labels={Label}&state=open&per_page=100";
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var issues = await resp.Content.ReadFromJsonAsync<List<IssueRef>>(Json, ct) ?? new();
        var wanted = TitleFor(sourceId);
        return issues.FirstOrDefault(i => i.Title == wanted)?.Number;
    }

    private async Task CreateIssueAsync(string sourceId, string error, int consecutiveFailures, CancellationToken ct)
    {
        var body = $$"""
        Scraper `{{sourceId}}` has failed {{consecutiveFailures}} runs in a row.

        Latest error:

        ```
        {{Truncate(error, 3500)}}
        ```

        Triggered by BabyBrain's daily scrape. This issue will be closed automatically on the next successful run.
        """;
        var payload = new { title = TitleFor(sourceId), body, labels = new[] { Label } };
        using var resp = await _http.PostAsJsonAsync($"/repos/{_owner}/{_repo}/issues", payload, Json, ct);
        resp.EnsureSuccessStatusCode();
    }

    private async Task PostCommentAsync(int issueNumber, string body, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync(
            $"/repos/{_owner}/{_repo}/issues/{issueNumber}/comments", new { body }, Json, ct);
        resp.EnsureSuccessStatusCode();
    }

    private async Task CloseIssueAsync(int issueNumber, CancellationToken ct)
    {
        using var resp = await _http.PatchAsJsonAsync(
            $"/repos/{_owner}/{_repo}/issues/{issueNumber}", new { state = "closed" }, Json, ct);
        resp.EnsureSuccessStatusCode();
    }

    private async Task EnsureLabelAsync(CancellationToken ct)
    {
        using var check = await _http.GetAsync($"/repos/{_owner}/{_repo}/labels/{Label}", ct);
        if (check.IsSuccessStatusCode) return;
        if (check.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            check.EnsureSuccessStatusCode(); // bubble up auth / permission errors
        }
        var payload = new { name = Label, color = LabelColor, description = LabelDescription };
        using var create = await _http.PostAsJsonAsync($"/repos/{_owner}/{_repo}/labels", payload, Json, ct);
        if (!create.IsSuccessStatusCode && create.StatusCode != System.Net.HttpStatusCode.UnprocessableEntity)
        {
            // 422 = label already exists (race with another writer). Anything else
            // is a real problem worth surfacing in the log.
            create.EnsureSuccessStatusCode();
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";

    private sealed class IssueRef
    {
        [JsonPropertyName("number")] public int Number { get; set; }
        [JsonPropertyName("title")] public string Title { get; set; } = "";
    }
}
