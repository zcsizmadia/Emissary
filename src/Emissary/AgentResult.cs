namespace Emissary;

/// <summary>Why an agent run ended.</summary>
public enum AgentStopReason
{
    /// <summary>The model finished its answer.</summary>
    Completed,

    /// <summary>The response hit <see cref="AgentOptions.MaxTokens"/>.</summary>
    MaxTokens,

    /// <summary>The model declined to answer.</summary>
    Refusal,

    /// <summary>The run hit <see cref="AgentOptions.MaxTurns"/> before converging.</summary>
    TurnLimit,

    /// <summary>The run hit <see cref="AgentOptions.TokenBudget"/>.</summary>
    BudgetExceeded,

    /// <summary>The run paused at a human-in-the-loop gate; see <see cref="AgentResult.Suspension"/>.</summary>
    AwaitingApproval,
}

/// <summary>Token usage accumulated across all turns of a run.</summary>
/// <param name="InputTokens">Total input tokens (cache reads and writes counted separately).</param>
/// <param name="OutputTokens">Total output tokens.</param>
/// <param name="CacheCreationInputTokens">Input tokens written to the prompt cache.</param>
/// <param name="CacheReadInputTokens">Input tokens served from the prompt cache.</param>
public sealed record AgentUsage(
    long InputTokens,
    long OutputTokens,
    long CacheCreationInputTokens = 0,
    long CacheReadInputTokens = 0)
{
    /// <summary>No usage.</summary>
    public static AgentUsage Zero { get; } = new(0, 0);

    /// <summary>Returns this usage plus one turn's tokens.</summary>
    /// <param name="inputTokens">The turn's input tokens.</param>
    /// <param name="outputTokens">The turn's output tokens.</param>
    /// <param name="cacheCreationInputTokens">The turn's cache-write tokens.</param>
    /// <param name="cacheReadInputTokens">The turn's cache-read tokens.</param>
    public AgentUsage Add(
        long inputTokens,
        long outputTokens,
        long cacheCreationInputTokens = 0,
        long cacheReadInputTokens = 0) =>
        new(
            InputTokens + inputTokens,
            OutputTokens + outputTokens,
            CacheCreationInputTokens + cacheCreationInputTokens,
            CacheReadInputTokens + cacheReadInputTokens);
}

/// <summary>The outcome of an agent run.</summary>
public sealed class AgentResult
{
    /// <summary>The full conversation including every assistant turn and tool result.</summary>
    public required Conversation Conversation { get; init; }

    /// <summary>Why the run ended.</summary>
    public required AgentStopReason StopReason { get; init; }

    /// <summary>Token usage summed over all turns.</summary>
    public required AgentUsage Usage { get; init; }

    /// <summary>
    /// Whether untrusted tool output (a tool marked <see cref="ClaudeToolAttribute.Untrusted"/>)
    /// entered the conversation during the run.
    /// </summary>
    public bool Tainted { get; init; }

    /// <summary>
    /// The privileged calls intercepted in <see cref="ExecutionMode.Shadow"/> runs — the plan of
    /// effects awaiting approval. Empty for live runs.
    /// </summary>
    public IReadOnlyList<PlannedEffect> PlannedEffects { get; init; } = [];

    /// <summary>
    /// The durable suspension state when <see cref="StopReason"/> is
    /// <see cref="AgentStopReason.AwaitingApproval"/>; otherwise <see langword="null"/>.
    /// </summary>
    public SuspendedRun? Suspension { get; init; }

    /// <summary>The text of the last assistant message, or "" if there is none.</summary>
    public string FinalText =>
        Conversation.Messages.LastOrDefault(m => m.Role == MessageRole.Assistant)?.Text ?? "";

    /// <summary>
    /// Deserializes <see cref="FinalText"/> as <typeparamref name="T"/> — for runs configured
    /// with <see cref="AgentOptions.OutputSchemaJson"/>. AOT-safe: pass source-generated metadata
    /// (<c>YourJsonContext.Default.YourType</c>).
    /// </summary>
    /// <typeparam name="T">The structured output type.</typeparam>
    /// <param name="typeInfo">Source-generated serializer metadata for <typeparamref name="T"/>.</param>
    /// <exception cref="InvalidOperationException">The final text is not a <typeparamref name="T"/>.</exception>
    public T FinalAs<T>(System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        System.Text.Json.JsonSerializer.Deserialize(FinalText, typeInfo)
            ?? throw new InvalidOperationException("The final assistant text deserialized to null.");
}
