using System.Collections.Concurrent;

namespace BabyBrain.Web.Services;

// In-memory tracker of currently-running scrapes so the Admin UI can show
// spinners and the API can refuse duplicate queued requests. Singleton —
// per-process; container restarts reset state (intentional — a dead process
// won't be "stuck running" after a fresh start).
public interface IScrapeStatusTracker
{
    // Atomically tries to mark a source as running. Returns true if it was
    // newly marked; false if it was already running (caller should not start
    // duplicate work).
    bool TryStart(string sourceId);

    // Mark a source as no longer running. Idempotent.
    void Finish(string sourceId);

    bool IsRunning(string sourceId);
    IReadOnlyCollection<string> RunningSources { get; }
}

public sealed class ScrapeStatusTracker : IScrapeStatusTracker
{
    private readonly ConcurrentDictionary<string, byte> _running = new(StringComparer.Ordinal);

    public bool TryStart(string sourceId) => _running.TryAdd(sourceId, 0);
    public void Finish(string sourceId) => _running.TryRemove(sourceId, out _);
    public bool IsRunning(string sourceId) => _running.ContainsKey(sourceId);
    public IReadOnlyCollection<string> RunningSources => _running.Keys.ToArray();
}
