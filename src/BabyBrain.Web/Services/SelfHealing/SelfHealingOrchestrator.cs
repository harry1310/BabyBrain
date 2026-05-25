using BabyBrain.Scrapers;

namespace BabyBrain.Web.Services.SelfHealing;

// Composes the three pieces of the heal pipeline:
//   1. Resolve the scraper class for the failing source.
//   2. Fetch its source file from GitHub (running container has no .cs files).
//   3. Ask Claude for a diagnosis and patches.
//   4. Open a draft PR via GitHub. Comment on the failure issue with the PR
//      number (or with Claude's diagnosis if no patches were produced).
//
// Every failure here is logged and swallowed — heal must never break a scrape.
public sealed class SelfHealingOrchestrator
{
    private readonly IClaudeHealer _healer;
    private readonly GitHubSourceFetcher _sourceFetcher;
    private readonly GitHubPatchPublisher _publisher;
    private readonly IEnumerable<IScraper> _scrapers;
    private readonly ILogger<SelfHealingOrchestrator> _logger;

    public SelfHealingOrchestrator(
        IClaudeHealer healer,
        GitHubSourceFetcher sourceFetcher,
        GitHubPatchPublisher publisher,
        IEnumerable<IScraper> scrapers,
        ILogger<SelfHealingOrchestrator> logger)
    {
        _healer = healer;
        _sourceFetcher = sourceFetcher;
        _publisher = publisher;
        _scrapers = scrapers;
        _logger = logger;
    }

    // Returns the PR number on success, or null when no PR was opened (no
    // diagnosis, no patches, idempotent skip, or any failure along the way).
    // Caller (GitHubScrapeAlertSink) uses null to skip the issue comment.
    public async Task<HealAttemptResult> TryHealAsync(string sourceId, string error, int issueNumber, CancellationToken ct = default)
    {
        var scraper = _scrapers.FirstOrDefault(s => s.SourceId == sourceId);
        if (scraper is null)
        {
            _logger.LogWarning("Heal skipped: no scraper found for source {Source}", sourceId);
            return HealAttemptResult.Failed("no_scraper_for_source");
        }

        var sourcePath = SourceFilePathFor(scraper.GetType());
        if (sourcePath is null) return HealAttemptResult.Failed("source_path_unresolved");

        var sourceFile = await _sourceFetcher.FetchAsync(sourcePath, ct);
        if (sourceFile is null)
        {
            _logger.LogWarning("Heal skipped: could not fetch source for {Source} at {Path}", sourceId, sourcePath);
            return HealAttemptResult.Failed($"source_fetch_failed:{sourcePath}");
        }

        var outcome = await _healer.DiagnoseAsync(sourceId, error, new[] { sourceFile }, ct);
        if (outcome.Result is null)
            return HealAttemptResult.Failed(outcome.FailureReason ?? "unknown_healer_failure");

        var heal = outcome.Result;
        if (heal.Patches.Count == 0)
        {
            // Claude returned a diagnosis but no actionable code change (e.g.
            // judged the failure transient). The caller posts the diagnosis as
            // an issue comment.
            return new HealAttemptResult(heal.Diagnosis, PullRequestNumber: null);
        }

        var prNumber = await _publisher.OpenDraftPrAsync(sourceId, issueNumber, heal, ct);
        return new HealAttemptResult(heal.Diagnosis, prNumber);
    }

    // Convention: namespace mirrors folder structure under src/BabyBrain.Scrapers/.
    // BabyBrain.Scrapers.WigmoreHall.WigmoreHallUnderFivesScraper ->
    //   src/BabyBrain.Scrapers/WigmoreHall/WigmoreHallUnderFivesScraper.cs
    private static string? SourceFilePathFor(Type scraperType)
    {
        const string prefix = "BabyBrain.Scrapers.";
        var ns = scraperType.Namespace;
        if (string.IsNullOrEmpty(ns) || !ns.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var sub = ns.Substring(prefix.Length).Replace('.', '/');
        return $"src/BabyBrain.Scrapers/{sub}/{scraperType.Name}.cs";
    }
}

// Reason carries a short token (e.g. "source_fetch_failed:...", "no_tool_use_in_response",
// "empty_diagnosis_and_no_patches", "claude_api_threw:HttpRequestException") for
// the cases where the orchestrator bailed without a usable diagnosis. It surfaces
// in the GitHub issue comment so a future reader can tell which path fired
// without having to grep server logs. Null on the happy paths (diagnosis-only or
// PR-opened) where the Diagnosis field is the useful signal.
public sealed record HealAttemptResult(string? Diagnosis, int? PullRequestNumber, string? Reason = null)
{
    public static readonly HealAttemptResult Empty = new(null, null);
    public static HealAttemptResult Failed(string reason) => new(null, null, reason);
}
