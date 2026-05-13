namespace BabyBrain.Web.Services.SelfHealing;

// What ClaudeHealer returns when asked to diagnose a scraper failure.
// - Diagnosis: a short root-cause sentence the model produced.
// - Patches: full file replacements that, applied together, should fix it.
//   May be empty if the model couldn't propose anything actionable, in which
//   case Diagnosis is still posted as a comment on the failure issue.
public sealed record HealResult(string Diagnosis, IReadOnlyList<HealPatch> Patches);

public sealed record HealPatch(string Path, string NewContent);
