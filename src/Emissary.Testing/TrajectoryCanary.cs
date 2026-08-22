using System.Text;

namespace Emissary.Testing;

/// <summary>One behavioral difference between a baseline trajectory and a candidate run.</summary>
/// <param name="Kind">A stable difference kind (see <see cref="CanaryDifference"/> constants).</param>
/// <param name="Description">A human-readable account of what changed.</param>
public sealed record CanaryDifference(string Kind, string Description)
{
    /// <summary>The sequence of tool calls changed.</summary>
    public const string ToolSequence = "tool_sequence";

    /// <summary>The number of model calls changed.</summary>
    public const string TurnCount = "turn_count";

    /// <summary>The run stopped for a different reason.</summary>
    public const string StopReason = "stop_reason";

    /// <summary>The final answer text changed (expected across model versions).</summary>
    public const string FinalText = "final_text";
}

/// <summary>The outcome of one canary comparison.</summary>
public sealed class CanaryReport
{
    /// <summary>The scenario that was re-run — the baseline's initial user message.</summary>
    public required string Scenario { get; init; }

    /// <summary>Every detected difference, in evaluation order.</summary>
    public required IReadOnlyList<CanaryDifference> Differences { get; init; }

    /// <summary>
    /// Behavioral equivalence: no differences except (possibly)
    /// <see cref="CanaryDifference.FinalText"/> — wording drift across model versions is
    /// expected; different tool behavior is not.
    /// </summary>
    public bool Passed => Differences.All(d => d.Kind == CanaryDifference.FinalText);

    /// <summary>Byte-for-byte equivalence: no differences at all.</summary>
    public bool Identical => Differences.Count == 0;

    /// <summary>Renders the report as readable text.</summary>
    public string ToText()
    {
        var builder = new StringBuilder();
        builder.Append("Scenario: ").AppendLine(Scenario);
        builder.Append("Result:   ")
            .AppendLine(Identical ? "identical" : Passed ? "passed (text drift only)" : "BEHAVIOR CHANGED");
        foreach (var difference in Differences)
        {
            builder.Append("  [").Append(difference.Kind).Append("] ").AppendLine(difference.Description);
        }

        return builder.ToString();
    }
}

/// <summary>
/// Model-upgrade canarying: re-run a recorded scenario against a candidate agent (a new model,
/// new prompt, new tools — or a replay for testing) and report how the behavior differs from
/// the baseline recording.
/// </summary>
public static class TrajectoryCanary
{
    /// <summary>The scenario a baseline recorded — its initial user message.</summary>
    /// <param name="baseline">The baseline trajectory.</param>
    public static string ScenarioOf(Trajectory baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        return baseline.Turns[0].Request.Messages[0].Text;
    }

    /// <summary>Re-runs the baseline's scenario through the candidate agent and compares.</summary>
    /// <param name="baseline">The recorded baseline.</param>
    /// <param name="candidate">The agent to evaluate — live or replay.</param>
    /// <param name="cancellationToken">Cancels the candidate run.</param>
    public static async Task<CanaryReport> RunAsync(
        Trajectory baseline,
        ClaudeAgent candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var result = await candidate.RunAsync(ScenarioOf(baseline), cancellationToken).ConfigureAwait(false);
        return Compare(baseline, result);
    }

    /// <summary>Compares a candidate run against the baseline recording.</summary>
    /// <param name="baseline">The recorded baseline.</param>
    /// <param name="candidate">The candidate run's outcome.</param>
    public static CanaryReport Compare(Trajectory baseline, AgentResult candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        var differences = new List<CanaryDifference>();

        string[] baselineTools = [.. baseline.Turns
            .SelectMany(t => t.Response.Content.OfType<ToolUseBlock>())
            .Select(u => u.Name)];
        string[] candidateTools = [.. candidate.Conversation.Messages
            .Where(m => m.Role == MessageRole.Assistant)
            .SelectMany(m => m.Content.OfType<ToolUseBlock>())
            .Select(u => u.Name)];
        if (!baselineTools.SequenceEqual(candidateTools, StringComparer.Ordinal))
        {
            differences.Add(new CanaryDifference(
                CanaryDifference.ToolSequence,
                $"baseline [{string.Join(" -> ", baselineTools)}], candidate [{string.Join(" -> ", candidateTools)}]"));
        }

        int candidateTurns = candidate.Conversation.Messages.Count(m => m.Role == MessageRole.Assistant);
        if (baseline.Turns.Count != candidateTurns)
        {
            differences.Add(new CanaryDifference(
                CanaryDifference.TurnCount,
                $"baseline {baseline.Turns.Count} model call(s), candidate {candidateTurns}"));
        }

        var baselineStop = baseline.Turns[^1].Response.StopReason switch
        {
            "max_tokens" => AgentStopReason.MaxTokens,
            "refusal" => AgentStopReason.Refusal,
            _ => AgentStopReason.Completed,
        };
        if (baselineStop != candidate.StopReason)
        {
            differences.Add(new CanaryDifference(
                CanaryDifference.StopReason,
                $"baseline {baselineStop}, candidate {candidate.StopReason}"));
        }

        string baselineText = string.Concat(
            baseline.Turns[^1].Response.Content.OfType<TextBlock>().Select(t => t.Text));
        if (!string.Equals(baselineText, candidate.FinalText, StringComparison.Ordinal))
        {
            differences.Add(new CanaryDifference(
                CanaryDifference.FinalText,
                $"baseline \"{Truncate(baselineText)}\", candidate \"{Truncate(candidate.FinalText)}\""));
        }

        return new CanaryReport { Scenario = ScenarioOf(baseline), Differences = differences };
    }

    private static string Truncate(string text) =>
        text.Length <= 80 ? text : text[..77] + "...";
}
