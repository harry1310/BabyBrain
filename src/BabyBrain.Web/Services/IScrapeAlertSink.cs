namespace BabyBrain.Web.Services;

// Pluggable hook called by ScrapeRunner after every scraper run. The intent is
// a thin "what just happened" notification — the implementation decides whether
// (and how) to surface it. Today: GitHub Issues (open on repeat failure, close
// on recovery); the noop variant is wired in when alerting isn't configured.
public interface IScrapeAlertSink
{
    // Called when a scraper run failed. `consecutiveFailures` includes the
    // current run, so the first failure of a streak has the value 1.
    Task OnFailureAsync(string sourceId, string error, int consecutiveFailures, CancellationToken ct = default);

    // Called when a scraper run succeeded AND at least one of its recent
    // predecessors had failed — i.e. the source has just recovered.
    Task OnRecoveryAsync(string sourceId, CancellationToken ct = default);
}
