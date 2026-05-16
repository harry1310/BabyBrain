using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BabyBrain.Web.Services;

// Thin wrapper over the GitHub Issues REST API, shared by everything that
// touches issues: the scrape-failure alert sink, the Admin "raise issue"
// action, and the 6-hour API-fallback service. Centralising it keeps the
// HTTP shapes — and the one auth'd HttpClient — in a single place.
public sealed class GitHubIssueClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _owner;
    private readonly string _repo;

    public GitHubIssueClient(HttpClient http, string owner, string repo)
    {
        _http = http;
        _owner = owner;
        _repo = repo;
    }

    public async Task EnsureLabelAsync(string name, string color, string description, CancellationToken ct = default)
    {
        using var check = await _http.GetAsync($"/repos/{_owner}/{_repo}/labels/{Uri.EscapeDataString(name)}", ct);
        if (check.IsSuccessStatusCode) return;
        if (check.StatusCode != HttpStatusCode.NotFound) check.EnsureSuccessStatusCode();

        var payload = new { name, color, description };
        using var create = await _http.PostAsJsonAsync($"/repos/{_owner}/{_repo}/labels", payload, Json, ct);
        // 422 = label already exists (race with another writer) — that's fine.
        if (!create.IsSuccessStatusCode && create.StatusCode != HttpStatusCode.UnprocessableEntity)
            create.EnsureSuccessStatusCode();
    }

    public async Task<int?> FindOpenIssueByTitleAsync(string title, string label, CancellationToken ct = default)
    {
        var issues = await ListOpenIssuesAsync(label, ct);
        return issues.FirstOrDefault(i => i.Title == title)?.Number;
    }

    public async Task<IReadOnlyList<GitHubIssue>> ListOpenIssuesAsync(string label, CancellationToken ct = default)
    {
        var url = $"/repos/{_owner}/{_repo}/issues?labels={Uri.EscapeDataString(label)}&state=open&per_page=100";
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var issues = await resp.Content.ReadFromJsonAsync<List<GitHubIssue>>(Json, ct) ?? new();
        // The /issues endpoint also returns pull requests — drop those.
        return issues.Where(i => i.PullRequest is null).ToList();
    }

    public async Task<GitHubIssueRef?> CreateIssueAsync(string title, string body, IEnumerable<string> labels, CancellationToken ct = default)
    {
        var payload = new { title, body, labels = labels.ToArray() };
        using var resp = await _http.PostAsJsonAsync($"/repos/{_owner}/{_repo}/issues", payload, Json, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<GitHubIssueRef>(Json, ct);
    }

    public async Task PostCommentAsync(int issueNumber, string body, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            $"/repos/{_owner}/{_repo}/issues/{issueNumber}/comments", new { body }, Json, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task AddLabelsAsync(int issueNumber, IEnumerable<string> labels, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            $"/repos/{_owner}/{_repo}/issues/{issueNumber}/labels", new { labels = labels.ToArray() }, Json, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task CloseIssueAsync(int issueNumber, CancellationToken ct = default)
    {
        using var resp = await _http.PatchAsJsonAsync(
            $"/repos/{_owner}/{_repo}/issues/{issueNumber}", new { state = "closed" }, Json, ct);
        resp.EnsureSuccessStatusCode();
    }
}

public sealed class GitHubIssue
{
    [JsonPropertyName("number")] public int Number { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("comments")] public int Comments { get; set; }
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("labels")] public List<GitHubLabel> Labels { get; set; } = new();

    // Present only when the item is actually a PR — used to filter those out.
    [JsonPropertyName("pull_request")] public JsonElement? PullRequest { get; set; }

    public bool HasLabel(string name) =>
        Labels.Any(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
}

public sealed class GitHubLabel
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

public sealed class GitHubIssueRef
{
    [JsonPropertyName("number")] public int Number { get; set; }
    [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = "";
}
