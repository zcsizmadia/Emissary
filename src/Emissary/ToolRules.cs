namespace Emissary;

/// <summary>
/// Declarative constraints on tool-call behavior, enforced by the agent loop at runtime.
/// A violating call is not executed; the model receives an error tool result explaining the
/// contract and can self-correct.
/// </summary>
public sealed class ToolRules
{
    internal Dictionary<string, string> Prerequisites { get; } = new(StringComparer.Ordinal);

    internal HashSet<string> Terminals { get; } = new(StringComparer.Ordinal);

    internal Dictionary<string, int> Limits { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The tool may only run after a prior <b>successful</b> call to
    /// <paramref name="prerequisite"/> — e.g. <c>refund_payment</c> requires
    /// <c>verify_identity</c>. Calls in the same parallel batch as the prerequisite do not count.
    /// </summary>
    /// <param name="tool">The guarded tool's wire name.</param>
    /// <param name="prerequisite">The tool that must have succeeded first.</param>
    public ToolRules Require(string tool, string prerequisite)
    {
        ArgumentException.ThrowIfNullOrEmpty(tool);
        ArgumentException.ThrowIfNullOrEmpty(prerequisite);
        Prerequisites[tool] = prerequisite;
        return this;
    }

    /// <summary>After this tool is called, no further tool calls are allowed in the run.</summary>
    /// <param name="tool">The terminal tool's wire name.</param>
    public ToolRules Terminal(string tool)
    {
        ArgumentException.ThrowIfNullOrEmpty(tool);
        Terminals.Add(tool);
        return this;
    }

    /// <summary>The tool may be called at most this many times per run (attempts count).</summary>
    /// <param name="tool">The limited tool's wire name.</param>
    /// <param name="maxCalls">The maximum number of calls.</param>
    public ToolRules Limit(string tool, int maxCalls)
    {
        ArgumentException.ThrowIfNullOrEmpty(tool);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCalls, 1);
        Limits[tool] = maxCalls;
        return this;
    }
}
