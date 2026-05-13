using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;

namespace BabyBrain.Web.Services.SelfHealing;

// Asks Claude (claude-opus-4-7 by default, overridable via BABYBRAIN_CLAUDE_MODEL)
// to look at a failing scraper and propose file changes. Structured output is
// guaranteed by forcing a single tool call to `propose_patches` — we never read
// free-text from the response.
public sealed class ClaudeHealer : IClaudeHealer
{
    private const string ToolName = "propose_patches";

    private const string SystemPrompt = """
        You are a maintenance assistant for BabyBrain, a small web-scraping
        service that aggregates London baby and toddler events. Scrapers are
        C# classes implementing IScraper and use AngleSharp to parse HTML.

        When a scraper fails repeatedly, you are given the source files and
        the exception. Propose the minimal change that should fix it —
        typically a CSS selector update because the source site changed its
        markup.

        Rules:
        - Each patch must contain the FULL new file contents, not a diff.
        - Preserve namespace, class names, public method signatures, and
          existing comments (including v1/v2 caveats and TODOs).
        - Only emit a patch for files that actually need to change.
        - If the failure looks transient (network timeout, 5xx, DNS), return
          an empty patches array with a diagnosis saying so.
        - If you cannot confidently diagnose from the given context, return
          an empty patches array and explain what extra context would help.
        - Keep the diagnosis to 1-3 sentences.
        """;

    private readonly AnthropicClient _client;
    private readonly string _model;
    private readonly ILogger<ClaudeHealer> _logger;

    public ClaudeHealer(AnthropicClient client, string model, ILogger<ClaudeHealer> logger)
    {
        _client = client;
        _model = model;
        _logger = logger;
    }

    public async Task<HealResult?> DiagnoseAsync(
        string sourceId,
        string error,
        IReadOnlyList<HealPatch> currentFiles,
        CancellationToken ct = default)
    {
        if (currentFiles.Count == 0)
        {
            _logger.LogWarning("ClaudeHealer called with no source files for {Source}", sourceId);
            return null;
        }

        try
        {
            var userMessage = BuildUserMessage(sourceId, error, currentFiles);
            var parameters = new MessageCreateParams
            {
                Model = _model,
                MaxTokens = 32000,
                Thinking = new ThinkingConfigAdaptive(),
                OutputConfig = new OutputConfig { Effort = Effort.High },
                System = new List<TextBlockParam>
                {
                    new() { Text = SystemPrompt },
                },
                Tools = [BuildTool()],
                ToolChoice = new ToolChoiceTool { Name = ToolName },
                Messages = [new() { Role = Role.User, Content = userMessage }],
            };

            var response = await _client.Messages.Create(parameters, cancellationToken: ct);
            return Parse(response, sourceId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Claude API call failed during heal of {Source}", sourceId);
            return null;
        }
    }

    private static string BuildUserMessage(string sourceId, string error, IReadOnlyList<HealPatch> currentFiles)
    {
        var sb = new StringBuilder();
        sb.Append("Scraper `").Append(sourceId).AppendLine("` has been failing on consecutive scheduled runs.");
        sb.AppendLine();
        sb.AppendLine("Latest exception (truncated to relevant frames):");
        sb.AppendLine("```");
        sb.AppendLine(error);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("Current source files (repo-relative paths):");
        foreach (var file in currentFiles)
        {
            sb.AppendLine();
            sb.Append("--- ").Append(file.Path).AppendLine(" ---");
            sb.AppendLine(file.NewContent);
        }
        return sb.ToString();
    }

    private static Tool BuildTool() => new()
    {
        Name = ToolName,
        Description = "Return the root-cause diagnosis and any file changes needed to fix the scraper.",
        InputSchema = new()
        {
            Properties = new Dictionary<string, JsonElement>
            {
                ["diagnosis"] = JsonSerializer.SerializeToElement(new
                {
                    type = "string",
                    description = "Concise root-cause explanation, 1-3 sentences.",
                }),
                ["patches"] = JsonSerializer.SerializeToElement(new
                {
                    type = "array",
                    description = "Files to overwrite. Each contains the full new file contents. Empty array if no actionable fix is possible.",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new { type = "string", description = "Repo-relative path of the file to overwrite." },
                            new_content = new { type = "string", description = "Full new file contents." },
                        },
                        required = new[] { "path", "new_content" },
                    },
                }),
            },
            Required = ["diagnosis", "patches"],
        },
    };

    private HealResult? Parse(Message response, string sourceId)
    {
        ToolUseBlock? toolUse = null;
        foreach (var block in response.Content)
        {
            if (block.TryPickToolUse(out var tu) && tu!.Name == ToolName)
            {
                toolUse = tu;
                break;
            }
        }
        if (toolUse is null)
        {
            _logger.LogWarning("Claude response had no {Tool} tool call for {Source}", ToolName, sourceId);
            return null;
        }

        var diagnosis = toolUse.Input.TryGetValue("diagnosis", out var diagEl) && diagEl.ValueKind == JsonValueKind.String
            ? diagEl.GetString() ?? ""
            : "";

        var patches = new List<HealPatch>();
        if (toolUse.Input.TryGetValue("patches", out var patchesEl) && patchesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in patchesEl.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object) continue;
                if (!p.TryGetProperty("path", out var pathEl) || pathEl.ValueKind != JsonValueKind.String) continue;
                if (!p.TryGetProperty("new_content", out var contentEl) || contentEl.ValueKind != JsonValueKind.String) continue;
                var path = pathEl.GetString();
                var content = contentEl.GetString();
                if (string.IsNullOrEmpty(path) || content is null) continue;
                patches.Add(new HealPatch(path, content));
            }
        }

        return new HealResult(diagnosis, patches);
    }
}
