namespace BabyBrain.Web.Services;

// Default sink when no alerting backend is configured — keeps ScrapeRunner's
// call site unconditional so the runner doesn't have to know about config.
public sealed class NoopScrapeAlertSink : IScrapeAlertSink
{
    public Task OnFailureAsync(string sourceId, string error, int consecutiveFailures, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task OnRecoveryAsync(string sourceId, CancellationToken ct = default)
        => Task.CompletedTask;
}
