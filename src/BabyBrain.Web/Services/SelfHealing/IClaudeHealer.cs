namespace BabyBrain.Web.Services.SelfHealing;

// Given a failing scraper's source code and the exception that triggered the
// alert, asks Claude to propose file changes that should fix it.
//
// Implementations call out to the Anthropic API; the NoopClaudeHealer is wired
// in when ANTHROPIC_API_KEY isn't configured so the rest of the heal pipeline
// stays disabled without throwing.
public interface IClaudeHealer
{
    Task<HealOutcome> DiagnoseAsync(
        string sourceId,
        string error,
        IReadOnlyList<HealPatch> currentFiles,
        CancellationToken ct = default);
}

public sealed class NoopClaudeHealer : IClaudeHealer
{
    public Task<HealOutcome> DiagnoseAsync(string sourceId, string error, IReadOnlyList<HealPatch> currentFiles, CancellationToken ct = default)
        => Task.FromResult(HealOutcome.Failed("anthropic_api_key_not_configured"));
}
