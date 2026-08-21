using System.Text.Json;
using Emissary.Serialization;

namespace Emissary;

/// <summary>Serializable snapshot of the tool-call guard for durable suspension.</summary>
/// <param name="Succeeded">Tools that have succeeded so far.</param>
/// <param name="Attempts">Attempt counts per tool.</param>
/// <param name="TerminatedBy">The terminal tool already called, if any.</param>
/// <param name="Tainted">Whether untrusted content has entered the run.</param>
/// <param name="TaintSource">The tool that introduced the taint, if any.</param>
public sealed record GuardSnapshot(
    IReadOnlyList<string> Succeeded,
    IReadOnlyDictionary<string, int> Attempts,
    string? TerminatedBy,
    bool Tainted,
    string? TaintSource);

/// <summary>
/// A durably suspended agent run, paused at a human-in-the-loop gate. Serialize with
/// <see cref="ToJson"/>, persist (e.g. via <see cref="IAgentStateStore"/>), and resume later —
/// minutes or days — with <see cref="ClaudeAgent.ResumeAsync"/>.
/// </summary>
/// <param name="ConversationId">The conversation's id.</param>
/// <param name="Messages">The conversation so far, ending with the assistant's tool-use turn.</param>
/// <param name="Usage">Usage accumulated before suspension.</param>
/// <param name="CompletedResults">Results of the batch's non-gated calls, already executed.</param>
/// <param name="PendingApprovals">The gated calls awaiting a decision.</param>
/// <param name="Guard">The tool-call guard state.</param>
/// <param name="PlannedEffects">Shadow-planned effects accumulated before suspension.</param>
public sealed record SuspendedRun(
    Guid ConversationId,
    IReadOnlyList<Message> Messages,
    AgentUsage Usage,
    IReadOnlyList<ToolResultBlock> CompletedResults,
    IReadOnlyList<PlannedEffect> PendingApprovals,
    GuardSnapshot Guard,
    IReadOnlyList<PlannedEffect> PlannedEffects)
{
    /// <summary>Serializes the suspended run as indented JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, EmissaryJsonContext.Default.SuspendedRun);

    /// <summary>Deserializes a suspended run from JSON.</summary>
    /// <param name="json">The suspended-run JSON.</param>
    /// <exception cref="InvalidOperationException">The JSON is the null literal.</exception>
    public static SuspendedRun FromJson(string json) =>
        JsonSerializer.Deserialize(json, EmissaryJsonContext.Default.SuspendedRun)
            ?? throw new InvalidOperationException("The suspended-run JSON deserialized to null.");
}
