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
}

/// <summary>Token usage accumulated across all turns of a run.</summary>
/// <param name="InputTokens">Total input tokens.</param>
/// <param name="OutputTokens">Total output tokens.</param>
public sealed record AgentUsage(long InputTokens, long OutputTokens)
{
    /// <summary>No usage.</summary>
    public static AgentUsage Zero { get; } = new(0, 0);

    /// <summary>Returns this usage plus one turn's tokens.</summary>
    /// <param name="inputTokens">The turn's input tokens.</param>
    /// <param name="outputTokens">The turn's output tokens.</param>
    public AgentUsage Add(long inputTokens, long outputTokens) =>
        new(InputTokens + inputTokens, OutputTokens + outputTokens);
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

    /// <summary>The text of the last assistant message, or "" if there is none.</summary>
    public string FinalText =>
        Conversation.Messages.LastOrDefault(m => m.Role == MessageRole.Assistant)?.Text ?? "";
}
