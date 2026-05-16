using System.Text.RegularExpressions;

namespace BabyBrain.Web.Services;

// Shared conventions for BabyBrain's GitHub issues so every producer and
// consumer agrees on labels and on the hidden machine-readable source marker.
// Both issue kinds — scrape failures and user-reported mistakes — carry the
// `claude-fix` label and follow the identical flow from creation onwards:
// an open Claude Code console polls for them and claims by commenting; the
// server-side IssueFallbackService runs the API healer if 6 hours pass with
// no comment.
public static partial class IssueConventions
{
    public const string ClaudeFix = "claude-fix";
    public const string ScrapeFailure = "scrape-failure";
    public const string ReportedMistake = "reported-mistake";
    public const string ApiAttempted = "api-attempted";

    // Hidden marker embedded in every issue body so the fallback service can
    // recover which scraper the issue concerns without parsing prose.
    public static string SourceMarker(string sourceId) => $"<!-- babybrain-source: {sourceId} -->";

    public static string? ParseSourceMarker(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        var m = SourceMarkerRegex().Match(body);
        return m.Success ? m.Groups["id"].Value : null;
    }

    [GeneratedRegex(@"<!--\s*babybrain-source:\s*(?<id>[^\s>]+)\s*-->")]
    private static partial Regex SourceMarkerRegex();

    // Idempotent — creates each label only if missing. Safe to call on every
    // issue creation; the labels normally already exist.
    public static async Task EnsureLabelsAsync(GitHubIssueClient github, CancellationToken ct = default)
    {
        await github.EnsureLabelAsync(ClaudeFix, "5319e7",
            "Queued for a Claude Code fix (console polls; API fallback after 6h).", ct);
        await github.EnsureLabelAsync(ScrapeFailure, "d73a4a",
            "An automated scraper has failed multiple runs in a row.", ct);
        await github.EnsureLabelAsync(ReportedMistake, "fbca04",
            "A user reported incorrect event data via the site.", ct);
        await github.EnsureLabelAsync(ApiAttempted, "c5def5",
            "The server-side Claude API fallback has already run on this issue.", ct);
    }
}
