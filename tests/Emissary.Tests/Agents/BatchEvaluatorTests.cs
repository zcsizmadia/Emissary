using Emissary.Testing;
using Emissary.Tests.Agents;
using Emissary.Transport;

namespace Emissary.Tests;

public sealed class BatchEvaluatorTests
{
    private static AgentResult Run(string userText) => new()
    {
        Conversation = Conversation.Start()
            .Append(Message.User(userText))
            .Append(new Message(MessageRole.Assistant, [new TextBlock("done")])),
        StopReason = AgentStopReason.Completed,
        Usage = AgentUsage.Zero,
    };

    private static EvaluationRubric Rubric() => new EvaluationRubric().Criterion("ok", "Was it handled?");

    /// <summary>A judge that answers every request from a queue of verdict payloads.</summary>
    private static ClaudeAgent Judge(params string[] verdictJson)
    {
        var transport = new FakeTransport();
        foreach (string json in verdictJson)
        {
            transport.EnqueueTurn(FakeTransport.TextTurn(json));
        }

        return new ClaudeAgent(new AgentOptions { OutputSchemaJson = EmissaryEval.JudgeSchema }, transport);
    }

    private const string Pass = """{"verdicts":[{"name":"ok","passed":true,"reason":"handled"}]}""";
    private const string Fail = """{"verdicts":[{"name":"ok","passed":false,"reason":"ignored the request"}]}""";

    [Test]
    public async Task Grades_every_item_and_aggregates()
    {
        var judge = Judge(Pass, Pass, Fail);
        var suite = new[] { (Rubric(), Run("one")), (Rubric(), Run("two")), (Rubric(), Run("three")) };

        var report = await BatchEvaluator.EvaluateAllAsync(suite, judge, maxConcurrency: 1);

        await Assert.That(report.Items.Count).IsEqualTo(3);
        await Assert.That(report.PassedCount).IsEqualTo(2);
        await Assert.That(report.FailedCount).IsEqualTo(1);
        await Assert.That(report.PassRate).IsEqualTo(2.0 / 3.0);
        await Assert.That(report.Passed).IsFalse();
    }

    [Test]
    public async Task All_passing_reports_a_clean_batch()
    {
        var report = await BatchEvaluator.EvaluateAllAsync(
            [(Rubric(), Run("one")), (Rubric(), Run("two"))], Judge(Pass, Pass), maxConcurrency: 1);

        await Assert.That(report.Passed).IsTrue();
        await Assert.That(report.PassRate).IsEqualTo(1.0);
        await Assert.That(report.ToText()).Contains("2/2 passed (100%)");
    }

    [Test]
    public async Task Items_keep_their_submission_order_and_labels()
    {
        var report = await BatchEvaluator.EvaluateAllAsync(
            [(Rubric(), Run("first request")), (Rubric(), Run("second request"))],
            Judge(Pass, Pass), maxConcurrency: 1);

        await Assert.That(report.Items[0].Index).IsEqualTo(0);
        await Assert.That(report.Items[0].Label).IsEqualTo("first request");
        await Assert.That(report.Items[1].Label).IsEqualTo("second request");
    }

    [Test]
    public async Task Long_labels_are_truncated()
    {
        var report = await BatchEvaluator.EvaluateAllAsync(
            [(Rubric(), Run(new string('x', 200)))], Judge(Pass), maxConcurrency: 1);

        await Assert.That(report.Items[0].Label).EndsWith("...");
        await Assert.That(report.Items[0].Label.Length).IsEqualTo(60);
    }

    [Test]
    public async Task A_run_without_user_input_still_gets_a_label()
    {
        var run = new AgentResult
        {
            Conversation = Conversation.Start().Append(new Message(MessageRole.Assistant, [new TextBlock("hi")])),
            StopReason = AgentStopReason.Completed,
            Usage = AgentUsage.Zero,
        };

        var report = await BatchEvaluator.EvaluateAllAsync([(Rubric(), run)], Judge(Pass), maxConcurrency: 1);

        await Assert.That(report.Items[0].Label).IsEqualTo("(no input)");
    }

    [Test]
    public async Task A_failing_judge_fails_only_its_own_item()
    {
        // The judge runs out of scripted turns on the second call and throws.
        var judge = Judge(Pass);
        var suite = new[] { (Rubric(), Run("one")), (Rubric(), Run("two")) };

        var report = await BatchEvaluator.EvaluateAllAsync(suite, judge, maxConcurrency: 1);

        await Assert.That(report.Items[0].Result!.Passed).IsTrue();
        await Assert.That(report.Items[1].Error).IsNotNull();
        await Assert.That(report.FailedCount).IsEqualTo(1);
        await Assert.That(report.ToText()).Contains("judge failed:");
    }

    [Test]
    public async Task Report_lists_failing_criteria_worst_first()
    {
        var report = await BatchEvaluator.EvaluateAllAsync(
            [(Rubric(), Run("bad one"))], Judge(Fail), maxConcurrency: 1);

        string text = report.ToText();
        await Assert.That(text).Contains("0/1 passed (0%)");
        await Assert.That(text).Contains("FAIL ok: ignored the request");
    }

    [Test]
    public async Task An_empty_suite_passes_vacuously()
    {
        var report = await BatchEvaluator.EvaluateAllAsync([], Judge(), maxConcurrency: 2);

        await Assert.That(report.Items.Count).IsEqualTo(0);
        await Assert.That(report.Passed).IsTrue();
        await Assert.That(report.PassRate).IsEqualTo(1.0);
    }

    [Test]
    public async Task Concurrency_is_bounded_but_still_grades_everything()
    {
        var judge = Judge(Pass, Pass, Pass, Pass);
        var suite = Enumerable.Range(0, 4).Select(i => (Rubric(), Run($"item {i}"))).ToArray();

        var report = await BatchEvaluator.EvaluateAllAsync(suite, judge, maxConcurrency: 2);

        await Assert.That(report.PassedCount).IsEqualTo(4);
    }

    [Test]
    public async Task Arguments_are_validated()
    {
        await Assert.That(async () => { await BatchEvaluator.EvaluateAllAsync(null!, Judge()); })
            .Throws<ArgumentNullException>();
        await Assert.That(async () => { await BatchEvaluator.EvaluateAllAsync([], null!); })
            .Throws<ArgumentNullException>();
        await Assert.That(async () => { await BatchEvaluator.EvaluateAllAsync([], Judge(), maxConcurrency: 0); })
            .Throws<ArgumentOutOfRangeException>();
    }
}
