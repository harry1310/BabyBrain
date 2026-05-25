namespace BabyBrain.Web.Services.SelfHealing;

// What ClaudeHealer returns when asked to diagnose a scraper failure.
// - Diagnosis: a short root-cause sentence the model produced.
// - Patches: full file replacements that, applied together, should fix it.
//   May be empty if the model couldn't propose anything actionable, in which
//   case Diagnosis is still posted as a comment on the failure issue.
public sealed record HealResult(string Diagnosis, IReadOnlyList<HealPatch> Patches);

public sealed record HealPatch(string Path, string NewContent);

// Outcome of asking the healer for a diagnosis. Either a usable HealResult, or
// a Failure with a short reason token that surfaces in the GitHub issue comment
// when the API fallback couldn't produce anything — without this, every failure
// path collapses into the same "produced no diagnosis or patch" message and we
// can't tell *why* without server logs.
public sealed record HealOutcome(HealResult? Result, string? FailureReason)
{
    public static HealOutcome Success(HealResult result) => new(result, null);
    public static HealOutcome Failed(string reason) => new(null, reason);
}
