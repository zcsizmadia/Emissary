using Emissary.Testing;
using Emissary.Tests.Agents;
using Emissary.Transport;

namespace Emissary.Tests;

public sealed class EvaluationTests
{
    private static AgentResult SampleRun()
    {
        var conversation = Conversation.Start()
            .Append(Message.User("Refund my order A-1"))
            .Append(new Message(MessageRole.Assistant, [new TextBlock("Done — refunded $10 to order A-1.")]));
        return new AgentResult
        {
            Conversation = conversation,
            StopReason = AgentStopReason.Completed,
            Usage = AgentUsage.Zero,
        };
    }

    private static ClaudeAgent JudgeReturning(string json)
    {
        // A replay judge: one turn that returns the verdicts JSON as its final text.
        var trajectory = new Trajectory(Trajectory.CurrentVersion,
        [
            new TrajectoryTurn(
                new TrajectoryRequest("claude-opus-5", null, 4096, ThinkingMode.Adaptive, null,
                    EmissaryEval.JudgeSchema, [Message.User("ignored")], []),
                new TrajectoryResponse([new TextBlock(json)], "end_turn", 10, 5)),
        ]);
        var options = new AgentOptions { OutputSchemaJson = EmissaryEval.JudgeSchema };
        return new ClaudeAgent(options, trajectory);
    }

    private static EvaluationRubric Rubric() => new EvaluationRubric()
        .Criterion("resolved", "Did the agent resolve the request?")
        .Criterion("polite", "Was the response polite?");

    [Test]
    public async Task All_criteria_pass_gives_full_score()
    {
        var judge = JudgeReturning(
            """{"verdicts":[{"name":"resolved","passed":true,"reason":"refund issued"},{"name":"polite","passed":true,"reason":"courteous"}]}""");

        var result = await EmissaryEval.EvaluateAsync(Rubric(), SampleRun(), judge);

        await Assert.That(result.Passed).IsTrue();
        await Assert.That(result.Score).IsEqualTo(1.0);
        await Assert.That(result.ToText()).Contains("PASSED");
    }

    [Test]
    public async Task Partial_pass_below_threshold_fails()
    {
        var judge = JudgeReturning(
            """{"verdicts":[{"name":"resolved","passed":true,"reason":"ok"},{"name":"polite","passed":false,"reason":"curt"}]}""");

        var result = await EmissaryEval.EvaluateAsync(Rubric(), SampleRun(), judge);

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.Score).IsEqualTo(0.5);
        await Assert.That(result.ToText()).Contains("[FAIL] polite");
    }

    [Test]
    public async Task Custom_threshold_allows_partial_pass()
    {
        var rubric = Rubric();
        rubric.PassThreshold = 0.5;
        var judge = JudgeReturning(
            """{"verdicts":[{"name":"resolved","passed":true,"reason":"ok"},{"name":"polite","passed":false,"reason":"curt"}]}""");

        var result = await EmissaryEval.EvaluateAsync(rubric, SampleRun(), judge);

        await Assert.That(result.Passed).IsTrue();
    }

    [Test]
    public async Task Missing_verdict_is_treated_as_a_failure()
    {
        var judge = JudgeReturning(
            """{"verdicts":[{"name":"resolved","passed":true,"reason":"ok"}]}""");

        var result = await EmissaryEval.EvaluateAsync(Rubric(), SampleRun(), judge);

        var polite = result.Results.Single(r => r.Name == "polite");
        await Assert.That(polite.Passed).IsFalse();
        await Assert.That(polite.Reason).Contains("no verdict");
    }

    [Test]
    public async Task Empty_rubric_passes_vacuously()
    {
        var judge = JudgeReturning("""{"verdicts":[]}""");

        var result = await EmissaryEval.EvaluateAsync(new EvaluationRubric(), SampleRun(), judge);

        await Assert.That(result.Score).IsEqualTo(1.0);
        await Assert.That(result.Passed).IsTrue();
    }

    [Test]
    public async Task Judge_prompt_includes_criteria_and_transcript()
    {
        string prompt = EmissaryEval.BuildJudgePrompt(Rubric(), SampleRun());

        await Assert.That(prompt).Contains("resolved: Did the agent resolve");
        await Assert.That(prompt).Contains("refunded $10");
        await Assert.That(prompt).Contains("User:");
    }

    [Test]
    public async Task Judge_prompt_renders_tool_calls_and_results()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("{}");
        var conversation = Conversation.Start()
            .Append(Message.User("go"))
            .Append(new Message(MessageRole.Assistant,
                [new ThinkingBlock("planning", "sig"), new ToolUseBlock("t1", "refund", doc.RootElement.Clone())]))
            .Append(new Message(MessageRole.User, [new ToolResultBlock("t1", "refunded", false)]))
            .Append(new Message(MessageRole.Assistant, [new TextBlock("all set")]));
        var run = new AgentResult { Conversation = conversation, StopReason = AgentStopReason.Completed, Usage = AgentUsage.Zero };

        string prompt = EmissaryEval.BuildJudgePrompt(new EvaluationRubric().Criterion("x", "y?"), run);

        await Assert.That(prompt).Contains("[calls refund]");
        await Assert.That(prompt).Contains("[tool result: refunded]");
    }

    [Test]
    public async Task Rubric_and_eval_validate_arguments()
    {
        var rubric = new EvaluationRubric();
        await Assert.That(() => rubric.Criterion("", "q")).Throws<ArgumentException>();
        await Assert.That(() => rubric.Criterion("n", "")).Throws<ArgumentException>();
        await Assert.That(() => EmissaryEval.BuildJudgePrompt(null!, SampleRun())).Throws<ArgumentNullException>();
        await Assert.That(() => EmissaryEval.BuildJudgePrompt(rubric, null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => { await EmissaryEval.EvaluateAsync(rubric, SampleRun(), null!); })
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Judge_schema_is_strict()
    {
        await Assert.That(EmissaryEval.JudgeSchema).Contains("\"additionalProperties\":false");
    }
}
