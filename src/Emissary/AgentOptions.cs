namespace Emissary;

/// <summary>How Claude's extended thinking is configured.</summary>
public enum ThinkingMode
{
    /// <summary>Claude decides when and how much to think (recommended for Claude 4.6+).</summary>
    Adaptive,

    /// <summary>No thinking.</summary>
    Disabled,
}

/// <summary>How Emissary manages prompt caching.</summary>
public enum PromptCacheMode
{
    /// <summary>
    /// Cache breakpoints are placed automatically: after the tool definitions, after the system
    /// prompt, and after the latest message — so every follow-up turn reads the stable prefix
    /// from cache. Recommended for multi-turn agents.
    /// </summary>
    Automatic,

    /// <summary>No cache breakpoints are sent.</summary>
    None,
}

/// <summary>The effort budget for a response.</summary>
public enum EffortLevel
{
    /// <summary>Fast and cheap.</summary>
    Low,

    /// <summary>Balanced.</summary>
    Medium,

    /// <summary>Thorough.</summary>
    High,

    /// <summary>Maximum quality.</summary>
    Max,
}

/// <summary>Project-wide defaults.</summary>
public static class EmissaryDefaults
{
    /// <summary>The default Claude model.</summary>
    public const string Model = "claude-opus-5";
}

/// <summary>Configuration for a <see cref="ClaudeAgent"/>.</summary>
public sealed class AgentOptions
{
    /// <summary>The Claude model id. Defaults to <see cref="EmissaryDefaults.Model"/>.</summary>
    public string Model { get; set; } = EmissaryDefaults.Model;

    /// <summary>The system prompt, or <see langword="null"/> for none.</summary>
    public string? SystemPrompt { get; set; }

    /// <summary>The per-response output token limit.</summary>
    public int MaxTokens { get; set; } = 4096;

    /// <summary>
    /// The maximum number of model turns per run — the loop guard against a tool-use cycle
    /// that never converges.
    /// </summary>
    public int MaxTurns { get; set; } = 16;

    /// <summary>The tools available to the agent, typically generated <c>{Method}Tool</c> properties.</summary>
    public IList<ToolDefinition> Tools { get; } = [];

    /// <summary>Extended thinking configuration.</summary>
    public ThinkingMode Thinking { get; set; } = ThinkingMode.Adaptive;

    /// <summary>Prompt-cache management; automatic breakpoints by default.</summary>
    public PromptCacheMode PromptCaching { get; set; } = PromptCacheMode.Automatic;

    /// <summary>
    /// Optional hard cap on total tokens (input + output) for a run. When a turn pushes the
    /// accumulated usage to or past the budget, the run stops with
    /// <see cref="AgentStopReason.BudgetExceeded"/> before making another model call.
    /// </summary>
    public long? TokenBudget { get; set; }

    /// <summary>Optional effort budget; <see langword="null"/> uses the model default.</summary>
    public EffortLevel? Effort { get; set; }

    /// <summary>
    /// Optional strict JSON Schema the final answer must conform to (structured outputs) —
    /// typically a generated <c>[ClaudeSchema]</c> type's <c>JsonSchema</c> property.
    /// Parse the result with <see cref="AgentResult.FinalAs{T}"/>.
    /// </summary>
    public string? OutputSchemaJson { get; set; }

    /// <summary>API key override; <see langword="null"/> uses the ANTHROPIC_API_KEY environment variable.</summary>
    public string? ApiKey { get; set; }
}
