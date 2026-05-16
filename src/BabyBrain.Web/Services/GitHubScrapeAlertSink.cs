namespace BabyBrain.Web.Services;

// Opens a GitHub Issue when a scraper has failed two runs in a row, and closes
// it on the next successful run. Idempotent: one open issue per source at most,
// keyed by the `scrape-failure` label plus a stable title.
//
// The issue also carries the shared `claude-fix` label, which puts it into the
// same queue as user-reported mistakes: an open Claude Code console polls for
// it and claims by commenting; failing that, IssueFallbackService runs the
// Claude API healer 6 hours later. The sink itself no longer triggers any
// healing — it only raises and closes the issue.
//
// Failure modes (no network, bad token, missing repo) are caught and logged —
// alerting must never break the scrape.
public sealed class GitHubScrapeAlertSink : IScrapeAlertSink
{
    private const int OpenIssueThreshold = 2; // failures-in-a-row before alerting

    private readonly GitHubIssueClient _github;
    private readonly ILogger<GitHubScrapeAlertSink> _logger;

    public GitHubScrapeAlertSink(GitHubIssueClient github, ILogger<GitHubScrapeAlertSink> logger)
    {
        _github = github;
        _logger = logger;
    }

    public async Task OnFailureAsync(string sourceId, string error, int consecutiveFailures, CancellationToken ct = default)
    {
        if (consecutiveFailures < OpenIssueThreshold) return;
        try
        {
            await IssueConventions.EnsureLabelsAsync(_github, ct);
            if (await _github.FindOpenIssueByTitleAsync(TitleFor(sourceId), IssueConventions.ScrapeFailure, ct) is not null)
                return; // already alerted on this streak

            var created = await _github.CreateIssueAsync(
                TitleFor(sourceId),
                BuildBody(sourceId, error, consecutiveFailures),
                new[] { IssueConventions.ScrapeFailure, IssueConventions.ClaudeFix },
                ct);
            _logger.LogInformation(
                "Opened GitHub issue #{Number} for failing scraper {Source}", created?.Number, sourceId);
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
            if (await _github.FindOpenIssueByTitleAsync(TitleFor(sourceId), IssueConventions.ScrapeFailure, ct) is not { } number)
                return;
            await _github.PostCommentAsync(number, "Recovered — next scrape succeeded. Closing.", ct);
            await _github.CloseIssueAsync(number, ct);
            _logger.LogInformation("Closed GitHub issue #{Number} for recovered scraper {Source}", number, sourceId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GitHub alert (recovery) failed for {Source}", sourceId);
        }
    }

    private static string TitleFor(string sourceId) => $"Scrape failing: {sourceId}";

    private static string BuildBody(string sourceId, string error, int consecutiveFailures) => $$"""
        Scraper `{{sourceId}}` has failed {{consecutiveFailures}} runs in a row.

        Latest error:

        ```
        {{Truncate(error, 3500)}}
        ```

        Triggered by BabyBrain's daily scrape. This issue will be closed automatically on the next successful run.

        {{IssueConventions.SourceMarker(sourceId)}}
        """;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";
}
