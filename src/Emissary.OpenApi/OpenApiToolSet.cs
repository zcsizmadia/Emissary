using System.Text;

namespace Emissary.OpenApi;

/// <summary>An operation the reader could not turn into a tool, and why.</summary>
/// <param name="Operation">The operation, as <c>METHOD /path</c>.</param>
/// <param name="Reason">Why it was skipped.</param>
public sealed record SkippedOperation(string Operation, string Reason);

/// <summary>
/// The tools read out of a specification, together with the operations that were skipped.
/// </summary>
/// <remarks>
/// The skipped list is not decoration. A specification that silently produces thirty fewer tools
/// than its author expects is a debugging session; printing <see cref="ToText"/> once at startup
/// turns it into a line of output.
/// </remarks>
public sealed class OpenApiToolSet
{
    internal OpenApiToolSet(IReadOnlyList<ToolDefinition> tools, IReadOnlyList<SkippedOperation> skipped)
    {
        Tools = tools;
        Skipped = skipped;
    }

    /// <summary>The generated tools, ready to add to <see cref="AgentOptions.Tools"/>.</summary>
    public IReadOnlyList<ToolDefinition> Tools { get; }

    /// <summary>The operations that produced no tool.</summary>
    public IReadOnlyList<SkippedOperation> Skipped { get; }

    /// <summary>A human-readable summary: what was generated, and what was left out.</summary>
    public string ToText()
    {
        var text = new StringBuilder();
        text.Append(Tools.Count).Append(Tools.Count == 1 ? " tool" : " tools").Append(": ");
        text.AppendLine(string.Join(", ", Tools.Select(t => t.Name)));

        if (Skipped.Count > 0)
        {
            text.Append(Skipped.Count).AppendLine(" operation(s) skipped:");
            foreach (var skipped in Skipped)
            {
                text.Append("  ").Append(skipped.Operation).Append(" — ").AppendLine(skipped.Reason);
            }
        }

        return text.ToString();
    }
}
