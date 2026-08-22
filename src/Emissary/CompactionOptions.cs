namespace Emissary;

/// <summary>
/// Client-side context compaction: when a turn's input grows past
/// <see cref="TriggerInputTokens"/>, Emissary summarizes the older part of the conversation
/// and replaces it with that summary, so long-running agents survive past the context window.
/// </summary>
/// <remarks>
/// Compaction is performed by Emissary (one extra model call), not by the server, so it appears
/// in trajectories, replays deterministically, and is visible as an
/// <see cref="AgentCompactedEvent"/> — the auditability the server-side alternative cannot offer.
/// </remarks>
public sealed class CompactionOptions
{
    /// <summary>
    /// Compact before the next model call once a response reports more input tokens than this.
    /// <see langword="null"/> (the default) disables compaction.
    /// </summary>
    public int? TriggerInputTokens { get; set; }

    /// <summary>
    /// How many recent messages to preserve verbatim. The real boundary is the nearest older
    /// assistant message, so tool-call/result pairs are never split. Default 6.
    /// </summary>
    public int KeepRecentMessages { get; set; } = 6;

    /// <summary>The instruction given to the model when summarizing the older messages.</summary>
    public string SummaryInstruction { get; set; } =
        "Summarize the conversation so far. Preserve every fact, decision, identifier, and " +
        "outstanding task the assistant needs to continue correctly. Be concise and factual.";
}
