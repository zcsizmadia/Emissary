namespace Emissary;

/// <summary>
/// Marks a static method as a Claude tool. The Emissary source generator turns it into a
/// <see cref="ToolDefinition"/> exposed as a generated <c>{MethodName}Tool</c> property on the
/// containing type — with a compile-time JSON Schema and a reflection-free dispatcher.
/// </summary>
/// <remarks>
/// The containing type (and any types it is nested in) must be declared <c>partial</c>.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ClaudeToolAttribute : Attribute
{
    /// <summary>
    /// The wire name of the tool. Defaults to the snake_case form of the method name.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// The tool description shown to the model. A missing description raises diagnostic EMS001 —
    /// Claude picks tools by their descriptions, so an empty one is almost always a bug.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Marks the tool's output as untrusted (web content, user documents, external email...).
    /// A successful call taints the rest of the run: <see cref="Privileged"/> tools are then
    /// blocked — the information-flow guard against prompt injection.
    /// </summary>
    public bool Untrusted { get; set; }

    /// <summary>
    /// Marks the tool as privileged (payments, deletions, outbound messages...). Privileged tools
    /// are blocked for the rest of a run once any <see cref="Untrusted"/> tool output has entered
    /// the conversation.
    /// </summary>
    public bool Privileged { get; set; }

    /// <summary>
    /// Caps how many characters of this tool's output reach the model, guarding the context
    /// window and token budget against a tool that returns far more than expected. Output past
    /// the cap is replaced with a notice telling the model data was withheld. Leave at
    /// <c>0</c> (the default) for no cap; negative values raise diagnostic EMS011.
    /// </summary>
    public int MaxResultLength { get; set; }

    /// <summary>
    /// Names another <c>[ClaudeTool]</c> method on the same type that undoes this tool's effect
    /// (saga compensation), e.g. <c>CompensatedBy = nameof(CancelReservation)</c>. The compensator
    /// is invoked with this tool's original input by <see cref="ClaudeAgent.CompensateAsync"/>.
    /// </summary>
    public string? CompensatedBy { get; set; }
}
