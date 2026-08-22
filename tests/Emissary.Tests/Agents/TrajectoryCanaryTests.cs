using Emissary.Testing;
using Emissary.Tests.Agents;
using Emissary.Tests.Tools;
using Emissary.Transport;

namespace Emissary.Tests;

public sealed class TrajectoryCanaryTests
{
    private static AgentOptions EchoOptions() => new() { Tools = { SampleTools.EchoTool } };

    private static async Task<Trajectory> Record(params StreamEvent[][] turns)
    {
        var recorder = new TrajectoryRecorder();
        var fake = new FakeTransport();
        foreach (var turn in turns)
        {
            fake.EnqueueTurn(turn);
        }

        var agent = new ClaudeAgent(EchoOptions(), new RecordingTransport(fake, recorder));
        await agent.RunAsync("run the scenario");
        return recorder.ToTrajectory();
    }

    private static Task<Trajectory> RecordToolLoop(string finalText) => Record(
        [FakeTransport.ToolTurn(FakeTransport.Use("t1", "echo", """{"text":"ping"}"""))],
        [FakeTransport.TextTurn(finalText)]);

    [Test]
    public async Task Identical_replay_produces_an_identical_report()
    {
        var baseline = await RecordToolLoop("done");
        var candidate = new ClaudeAgent(EchoOptions(), baseline);

        var report = await TrajectoryCanary.RunAsync(baseline, candidate);

        await Assert.That(report.Identical).IsTrue();
        await Assert.That(report.Passed).IsTrue();
        await Assert.That(report.Scenario).IsEqualTo("run the scenario");
        await Assert.That(report.ToText()).Contains("identical");
    }

    [Test]
    public async Task Text_drift_alone_passes_but_is_not_identical()
    {
        var baseline = await RecordToolLoop("done");
        var candidateRun = await RecordToolLoop("all finished!");
        var candidate = new ClaudeAgent(EchoOptions(), candidateRun);

        var report = await TrajectoryCanary.RunAsync(baseline, candidate);

        await Assert.That(report.Identical).IsFalse();
        await Assert.That(report.Passed).IsTrue();
        await Assert.That(report.Differences.Single().Kind).IsEqualTo(CanaryDifference.FinalText);
        await Assert.That(report.ToText()).Contains("passed (text drift only)");
    }

    [Test]
    public async Task Tool_behavior_changes_fail_the_canary()
    {
        var baseline = await RecordToolLoop("done");
        var candidateRun = await Record(
            [FakeTransport.ToolTurn(
                FakeTransport.Use("t1", "echo", """{"text":"a"}"""),
                FakeTransport.Use("t2", "echo", """{"text":"b"}"""))],
            [FakeTransport.TextTurn("done")]);
        var candidate = new ClaudeAgent(EchoOptions(), candidateRun);

        var report = await TrajectoryCanary.RunAsync(baseline, candidate);

        await Assert.That(report.Passed).IsFalse();
        await Assert.That(report.Differences.Select(d => d.Kind)).Contains(CanaryDifference.ToolSequence);
        await Assert.That(report.ToText()).Contains("BEHAVIOR CHANGED");
        await Assert.That(report.ToText()).Contains("echo -> echo");
    }

    [Test]
    public async Task Turn_count_changes_are_reported()
    {
        var baseline = await RecordToolLoop("done");
        var candidateRun = await Record([FakeTransport.TextTurn("done")]);
        var candidate = new ClaudeAgent(EchoOptions(), candidateRun);

        var report = await TrajectoryCanary.RunAsync(baseline, candidate);

        await Assert.That(report.Differences.Select(d => d.Kind)).Contains(CanaryDifference.TurnCount);
        await Assert.That(report.Differences.Select(d => d.Kind)).Contains(CanaryDifference.ToolSequence);
    }

    [Test]
    public async Task Stop_reason_changes_are_reported()
    {
        var baseline = await Record([FakeTransport.TextTurn("done")]);
        var candidateRun = await Record([FakeTransport.TextTurn("partial", "max_tokens")]);
        var candidate = new ClaudeAgent(EchoOptions(), candidateRun);

        var report = await TrajectoryCanary.RunAsync(baseline, candidate);

        await Assert.That(report.Passed).IsFalse();
        var stop = report.Differences.Single(d => d.Kind == CanaryDifference.StopReason);
        await Assert.That(stop.Description).Contains("baseline Completed, candidate MaxTokens");
    }

    [Test]
    [Arguments("max_tokens", "MaxTokens")]
    [Arguments("refusal", "Refusal")]
    public async Task Abnormal_baseline_stop_reasons_map_correctly(string wireStop, string expected)
    {
        var baseline = await Record([FakeTransport.TextTurn("partial", wireStop)]);
        var candidateRun = await Record([FakeTransport.TextTurn("partial")]);
        var candidate = new ClaudeAgent(EchoOptions(), candidateRun);

        var report = await TrajectoryCanary.RunAsync(baseline, candidate);

        var stop = report.Differences.Single(d => d.Kind == CanaryDifference.StopReason);
        await Assert.That(stop.Description).Contains($"baseline {expected}, candidate Completed");
    }

    [Test]
    public async Task Long_final_texts_are_truncated_in_descriptions()
    {
        var baseline = await Record([FakeTransport.TextTurn(new string('a', 200))]);
        var candidateRun = await Record([FakeTransport.TextTurn("short")]);
        var candidate = new ClaudeAgent(EchoOptions(), candidateRun);

        var report = await TrajectoryCanary.RunAsync(baseline, candidate);

        var text = report.Differences.Single(d => d.Kind == CanaryDifference.FinalText);
        await Assert.That(text.Description).Contains("...");
    }

    [Test]
    public async Task Canary_validates_arguments()
    {
        var baseline = await Record([FakeTransport.TextTurn("done")]);

        await Assert.That(() => TrajectoryCanary.ScenarioOf(null!)).Throws<ArgumentNullException>();
        await Assert.That(() => TrajectoryCanary.Compare(null!, null!)).Throws<ArgumentNullException>();
        await Assert.That(() => TrajectoryCanary.Compare(baseline, null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => { await TrajectoryCanary.RunAsync(baseline, null!); })
            .Throws<ArgumentNullException>();
    }
}
