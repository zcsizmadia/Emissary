using System.Text.Json;

namespace Emissary;

/// <summary>
/// One privileged tool call intercepted by <see cref="ExecutionMode.Shadow"/>: what the agent
/// would have done. Review the plan, then re-run live to commit.
/// </summary>
/// <param name="ToolName">The wire name of the intercepted tool.</param>
/// <param name="ToolUseId">The tool-use id in the conversation.</param>
/// <param name="Input">The exact input the tool would have received.</param>
public sealed record PlannedEffect(string ToolName, string ToolUseId, JsonElement Input);
