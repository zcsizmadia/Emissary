using System.Text.Json.Serialization;

namespace Emissary.Testing;

/// <summary>A single graded criterion in an <see cref="EvaluationRubric"/>.</summary>
/// <param name="Name">A short identifier, e.g. "correctness".</param>
/// <param name="Question">The yes/no question the judge answers about the run.</param>
public sealed record EvaluationCriterion(string Name, string Question);

/// <summary>A set of criteria a judge grades an agent run against, plus a pass threshold.</summary>
public sealed class EvaluationRubric
{
    private readonly List<EvaluationCriterion> _criteria = [];

    /// <summary>The criteria, in order.</summary>
    public IReadOnlyList<EvaluationCriterion> Criteria => _criteria;

    /// <summary>Fraction of criteria that must pass for the run to pass overall (default 1.0 = all).</summary>
    public double PassThreshold { get; set; } = 1.0;

    /// <summary>Adds a criterion.</summary>
    /// <param name="name">A short identifier.</param>
    /// <param name="question">The yes/no question the judge answers.</param>
    public EvaluationRubric Criterion(string name, string question)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(question);
        _criteria.Add(new EvaluationCriterion(name, question));
        return this;
    }
}

/// <summary>The judge's verdict on one criterion.</summary>
/// <param name="Name">The criterion name.</param>
/// <param name="Passed">Whether the run satisfied it.</param>
/// <param name="Reason">The judge's one-line justification.</param>
public sealed record CriterionResult(string Name, bool Passed, string Reason);

/// <summary>The graded outcome of evaluating one agent run against a rubric.</summary>
public sealed class EvaluationResult
{
    /// <summary>Per-criterion verdicts, in rubric order.</summary>
    public required IReadOnlyList<CriterionResult> Results { get; init; }

    /// <summary>The fraction of criteria that passed (0..1).</summary>
    public double Score => Results.Count == 0 ? 1.0 : (double)Results.Count(r => r.Passed) / Results.Count;

    /// <summary>Whether <see cref="Score"/> met the rubric's threshold.</summary>
    public required bool Passed { get; init; }

    /// <summary>Renders the verdicts as readable text.</summary>
    public string ToText()
    {
        var lines = Results.Select(r => $"  [{(r.Passed ? "PASS" : "FAIL")}] {r.Name}: {r.Reason}");
        return $"Evaluation {(Passed ? "PASSED" : "FAILED")} (score {Score:P0})\n{string.Join('\n', lines)}";
    }
}

/// <summary>The strict shape the judge model returns — one verdict per criterion.</summary>
internal sealed record JudgeVerdicts(JudgeVerdict[] Verdicts);

internal sealed record JudgeVerdict(string Name, bool Passed, string Reason);

[JsonSerializable(typeof(JudgeVerdicts))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class EvaluationJsonContext : JsonSerializerContext;

/// <summary>
/// LLM-as-judge evaluation: grade a completed agent run against a rubric using a judge agent.
/// The judge is an ordinary <see cref="ClaudeAgent"/>, so it can be a live model or a replayed
/// trajectory — which is how evaluations run deterministically in CI. Configure the judge with
/// <c>OutputSchemaJson = EmissaryEval.JudgeSchema</c> so it returns parseable verdicts.
/// </summary>
public static class EmissaryEval
{
    /// <summary>The strict JSON Schema the judge agent must be configured to produce.</summary>
    public static string JudgeSchema =>
        """{"type":"object","properties":{"verdicts":{"type":"array","items":{"type":"object","properties":{"name":{"type":"string"},"passed":{"type":"boolean"},"reason":{"type":"string"}},"required":["name","passed","reason"],"additionalProperties":false}}},"required":["verdicts"],"additionalProperties":false}""";

    /// <summary>Builds the judge prompt for a run and rubric (exposed for deterministic recording/replay).</summary>
    /// <param name="rubric">The criteria to grade.</param>
    /// <param name="run">The completed run to grade.</param>
    public static string BuildJudgePrompt(EvaluationRubric rubric, AgentResult run)
    {
        ArgumentNullException.ThrowIfNull(rubric);
        ArgumentNullException.ThrowIfNull(run);

        var transcript = string.Join('\n', run.Conversation.Messages.Select(m => $"{m.Role}: {RenderMessage(m)}"));
        var criteria = string.Join('\n', rubric.Criteria.Select(c => $"- {c.Name}: {c.Question}"));
        return "Grade the following agent transcript against each criterion. Answer each with " +
            $"pass/fail and a one-line reason.\n\nCRITERIA:\n{criteria}\n\nTRANSCRIPT:\n{transcript}";
    }

    /// <summary>Grades a run against a rubric using the judge agent.</summary>
    /// <param name="rubric">The criteria.</param>
    /// <param name="run">The completed run to grade.</param>
    /// <param name="judge">The judge agent (live or replay), configured with <see cref="JudgeSchema"/>.</param>
    /// <param name="cancellationToken">Cancels the judge call.</param>
    public static async Task<EvaluationResult> EvaluateAsync(
        EvaluationRubric rubric,
        AgentResult run,
        ClaudeAgent judge,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(judge);
        string prompt = BuildJudgePrompt(rubric, run);
        var verdicts = await judge.RunAsync(prompt, EvaluationJsonContext.Default.JudgeVerdicts, cancellationToken)
            .ConfigureAwait(false);
        return Grade(rubric, verdicts);
    }

    internal static EvaluationResult Grade(EvaluationRubric rubric, JudgeVerdicts verdicts)
    {
        var byName = new Dictionary<string, JudgeVerdict>(StringComparer.OrdinalIgnoreCase);
        foreach (var verdict in verdicts.Verdicts)
        {
            byName[verdict.Name] = verdict;
        }

        var results = rubric.Criteria.Select(c =>
            byName.TryGetValue(c.Name, out var v)
                ? new CriterionResult(c.Name, v.Passed, v.Reason)
                : new CriterionResult(c.Name, false, "The judge returned no verdict for this criterion."))
            .ToList();

        double score = results.Count == 0 ? 1.0 : (double)results.Count(r => r.Passed) / results.Count;
        return new EvaluationResult { Results = results, Passed = score >= rubric.PassThreshold };
    }

    private static string RenderMessage(Message message) =>
        string.Join(" ", message.Content.Select(b => b switch
        {
            TextBlock t => t.Text,
            ToolUseBlock u => $"[calls {u.Name}]",
            ToolResultBlock r => $"[tool result: {r.Content}]",
            _ => string.Empty,
        }));
}
