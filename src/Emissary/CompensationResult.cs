namespace Emissary;

/// <summary>The outcome of one saga compensation performed by <see cref="ClaudeAgent.CompensateAsync"/>.</summary>
/// <param name="ToolName">The compensated tool's wire name.</param>
/// <param name="ToolUseId">The original tool-use id.</param>
/// <param name="Success">Whether the compensation handler succeeded.</param>
/// <param name="Output">The handler's output, or the failure message.</param>
public sealed record CompensationResult(string ToolName, string ToolUseId, bool Success, string Output);
