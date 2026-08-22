using System.Text;

namespace Emissary.Testing;

/// <summary>One graded run inside a <see cref="BatchEvaluationReport"/>.</summary>
/// <param name="Index">The item's position in the submitted suite.</param>
/// <param name="Label">A human-readable label — the run's first user message, truncated.</param>
/// <param name="Result">The grading outcome, or <see langword="null"/> if the judge failed.</param>
/// <param name="Error">The judge failure message, or <see langword="null"/> on success.</param>
public sealed record BatchEvaluationItem(int Index, string Label, EvaluationResult? Result, string? Error);

/// <summary>The aggregate outcome of grading a suite of agent runs.</summary>
public sealed class BatchEvaluationReport
{
    /// <summary>Every graded item, in submission order.</summary>
    public required IReadOnlyList<BatchEvaluationItem> Items { get; init; }

    /// <summary>How many items passed their rubric.</summary>
    public int PassedCount => Items.Count(i => i.Result?.Passed == true);

    /// <summary>How many items failed their rubric or errored.</summary>
    public int FailedCount => Items.Count - PassedCount;

    /// <summary>The fraction of items that passed (1.0 for an empty suite).</summary>
    public double PassRate => Items.Count == 0 ? 1.0 : (double)PassedCount / Items.Count;

    /// <summary>Whether every item passed.</summary>
    public bool Passed => FailedCount == 0;

    /// <summary>Renders a summary followed by the failures, worst score first.</summary>
    public string ToText()
    {
        var builder = new StringBuilder();
        builder.Append("Batch evaluation: ").Append(PassedCount).Append('/').Append(Items.Count)
            .Append(" passed (").Append((PassRate * 100).ToString("F0", System.Globalization.CultureInfo.InvariantCulture))
            .AppendLine("%)");

        foreach (var item in Items.Where(i => i.Result?.Passed != true).OrderBy(i => i.Result?.Score ?? -1))
        {
            builder.Append("  [").Append(item.Index).Append("] ").AppendLine(item.Label);
            if (item.Error is { } error)
            {
                builder.Append("      judge failed: ").AppendLine(error);
                continue;
            }

            foreach (var criterion in item.Result!.Results.Where(r => !r.Passed))
            {
                builder.Append("      FAIL ").Append(criterion.Name).Append(": ").AppendLine(criterion.Reason);
            }
        }

        return builder.ToString();
    }
}

/// <summary>
/// Grades a whole suite of agent runs against rubrics with bounded concurrency — the shape you
/// want for a nightly quality gate over a set of golden trajectories.
/// </summary>
/// <remarks>
/// Judges run concurrently against the supplied agent. A judge that throws does not fail the
/// batch: that item is recorded with its error and counted as a failure, so one bad item never
/// discards the rest of the report.
/// </remarks>
public static class BatchEvaluator
{
    /// <summary>Grades every (rubric, run) pair and aggregates the outcomes.</summary>
    /// <param name="suite">The runs to grade, each with the rubric to grade it against.</param>
    /// <param name="judge">The judge agent, configured with <see cref="EmissaryEval.JudgeSchema"/>.</param>
    /// <param name="maxConcurrency">How many judge calls may be in flight at once. Default 4.</param>
    /// <param name="cancellationToken">Cancels the batch.</param>
    public static async Task<BatchEvaluationReport> EvaluateAllAsync(
        IEnumerable<(EvaluationRubric Rubric, AgentResult Run)> suite,
        ClaudeAgent judge,
        int maxConcurrency = 4,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(suite);
        ArgumentNullException.ThrowIfNull(judge);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);

        var items = suite.ToArray();
        var results = new BatchEvaluationItem[items.Length];
        using var limiter = new SemaphoreSlim(maxConcurrency, maxConcurrency);

        var tasks = new Task[items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            tasks[i] = GradeAsync(i, items[i].Rubric, items[i].Run);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return new BatchEvaluationReport { Items = results };

        async Task GradeAsync(int index, EvaluationRubric rubric, AgentResult run)
        {
            await limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var evaluation = await EmissaryEval.EvaluateAsync(rubric, run, judge, cancellationToken)
                    .ConfigureAwait(false);
                results[index] = new BatchEvaluationItem(index, Label(run), evaluation, null);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                results[index] = new BatchEvaluationItem(index, Label(run), null, exception.Message);
            }
            finally
            {
                limiter.Release();
            }
        }
    }

    private static string Label(AgentResult run)
    {
        string text = run.Conversation.Messages.FirstOrDefault(m => m.Role == MessageRole.User)?.Text ?? "(no input)";
        return text.Length <= 60 ? text : text[..57] + "...";
    }
}
