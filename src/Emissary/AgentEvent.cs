namespace Emissary;

/// <summary>One event in a streaming agent run.</summary>
public abstract record AgentEvent;

/// <summary>A fragment of assistant text as it streams.</summary>
/// <param name="Delta">The text fragment.</param>
public sealed record AgentTextEvent(string Delta) : AgentEvent;

/// <summary>A fragment of the model's thinking as it streams.</summary>
/// <param name="Delta">The thinking fragment.</param>
public sealed record AgentThinkingEvent(string Delta) : AgentEvent;

/// <summary>The model started calling a tool.</summary>
/// <param name="Id">The tool-use id.</param>
/// <param name="Name">The wire name of the tool.</param>
public sealed record AgentToolCallEvent(string Id, string Name) : AgentEvent;

/// <summary>A tool finished executing.</summary>
/// <param name="Id">The tool-use id.</param>
/// <param name="Name">The wire name of the tool.</param>
/// <param name="Result">The result content sent back to the model.</param>
/// <param name="IsError">Whether the tool failed.</param>
public sealed record AgentToolResultEvent(string Id, string Name, string Result, bool IsError) : AgentEvent;

/// <summary>One model turn completed and was appended to the conversation.</summary>
/// <param name="Assistant">The assistant message for the turn.</param>
public sealed record AgentTurnEvent(Message Assistant) : AgentEvent;

/// <summary>
/// The run paused at a human-in-the-loop gate. Persist the suspension and resume later with
/// <see cref="ClaudeAgent.ResumeAsync"/>. Followed by the final <see cref="AgentCompletedEvent"/>.
/// </summary>
/// <param name="Suspension">The serializable suspension state.</param>
public sealed record AgentSuspendedEvent(SuspendedRun Suspension) : AgentEvent;

/// <summary>
/// Older messages were summarized to keep the conversation inside the context window
/// (see <see cref="CompactionOptions"/>).
/// </summary>
/// <param name="MessagesSummarized">How many messages the summary replaced.</param>
/// <param name="Summary">The summary that replaced them.</param>
public sealed record AgentCompactedEvent(int MessagesSummarized, string Summary) : AgentEvent;

/// <summary>The run ended. Always the final event of a stream.</summary>
/// <param name="Result">The run outcome.</param>
public sealed record AgentCompletedEvent(AgentResult Result) : AgentEvent;
