namespace Emissary;

/// <summary>
/// Another agent this agent may transfer a conversation to. Unlike
/// <see cref="ClaudeAgent.AsTool"/> — where a sub-agent answers a question and control returns —
/// a handoff passes the whole conversation on: the target continues it with its own system
/// prompt, tools, and contracts, and produces the final answer.
/// </summary>
/// <param name="Name">A short identifier, e.g. "billing"; becomes the tool <c>handoff_to_billing</c>.</param>
/// <param name="Agent">The agent that takes over.</param>
/// <param name="Description">When to transfer, shown to the model choosing the target.</param>
public sealed record HandoffTarget(string Name, ClaudeAgent Agent, string Description);

/// <summary>A conversation was transferred to another agent.</summary>
/// <param name="TargetName">The <see cref="HandoffTarget.Name"/> that took over.</param>
/// <param name="Reason">Why the model transferred, if it gave a reason.</param>
public sealed record AgentHandoffEvent(string TargetName, string? Reason) : AgentEvent;
