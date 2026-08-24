using Emissary.Tests.Agents;
using Emissary.Tests.Tools;
using Emissary.Transport;

namespace Emissary.Tests;

/// <summary>
/// Replay has always re-executed tools, so a run whose tools touch the world touched it again.
/// A cassette serves what they returned instead, which is what makes a replay hermetic.
/// </summary>
public sealed class ToolCassetteTests
{
    /// <summary>A tool whose effect is observable, so a replay can be proven not to have run it.</summary>
    private sealed class Ledger
    {
        public List<string> Entries { get; } = [];

        public ToolDefinition Tool => new(
            "charge",
            "Charges an amount.",
            """{"type":"object","properties":{"amount":{"type":"integer"}}}""",
            (input, _) =>
            {
                int amount = input.GetProperty("amount").GetInt32();
                Entries.Add($"charged {amount}");
                return new ValueTask<string>($"charged {amount}");
            });
    }

    /// <summary>Records a run that calls the tool twice, then returns the trajectory.</summary>
    private static async Task<(Trajectory Trajectory, Ledger Ledger)> RecordAsync()
    {
        var ledger = new Ledger();
        var options = new AgentOptions { Model = "claude-test-1" };
        options.Tools.Add(ledger.Tool);

        var transport = new FakeTransport();
        transport.EnqueueTurn(FakeTransport.ToolTurn(
            FakeTransport.Use("t1", "charge", """{"amount":10}"""),
            FakeTransport.Use("t2", "charge", """{"amount":25}""")));
        transport.EnqueueTurn(FakeTransport.TextTurn("Charged 10 and 25."));

        var recorder = new TrajectoryRecorder();
        var agent = new ClaudeAgent(options, new RecordingTransport(transport, recorder));
        await agent.RunAsync("charge 10 then 25");

        return (recorder.ToTrajectory(), ledger);
    }

    private static (ClaudeAgent Agent, Ledger Ledger) Replaying(Trajectory trajectory, ToolReplayMode mode)
    {
        var ledger = new Ledger();
        var options = new AgentOptions { Model = "claude-test-1" };
        options.Tools.Add(ledger.Tool);
        return (new ClaudeAgent(options, trajectory, mode), ledger);
    }

    [Test]
    public async Task A_hermetic_replay_does_not_run_the_tools()
    {
        var (trajectory, recordedLedger) = await RecordAsync();
        await Assert.That(recordedLedger.Entries).IsEquivalentTo(["charged 10", "charged 25"]);

        var (agent, ledger) = Replaying(trajectory, ToolReplayMode.FromRecording);
        var result = await agent.RunAsync("charge 10 then 25");

        // The answer is identical, and the ledger was never touched.
        await Assert.That(result.FinalText).IsEqualTo("Charged 10 and 25.");
        await Assert.That(ledger.Entries).IsEmpty();

        // The recorded results still reached the model, so the run is the same run.
        var fedBack = result.Conversation.Messages
            .SelectMany(m => m.Content.OfType<ToolResultBlock>())
            .Select(r => r.Content)
            .ToList();
        await Assert.That(fedBack).IsEquivalentTo(["charged 10", "charged 25"]);
    }

    [Test]
    public async Task Replay_still_executes_tools_by_default()
    {
        var (trajectory, _) = await RecordAsync();

        var (agent, ledger) = Replaying(trajectory, ToolReplayMode.Execute);
        await agent.RunAsync("charge 10 then 25");

        // The long-standing behaviour: the model is replayed, the tools are not.
        await Assert.That(ledger.Entries).IsEquivalentTo(["charged 10", "charged 25"]);
    }

    [Test]
    public async Task A_call_the_recording_does_not_cover_is_a_divergence()
    {
        // A recording whose turn asks for a tool call, but which has no following turn carrying
        // the result — so the cassette cannot answer it.
        var uncovered = new Trajectory(Trajectory.CurrentVersion, [
            new TrajectoryTurn(
                new TrajectoryRequest(
                    "claude-test-1", null, 4096, ThinkingMode.Adaptive, null, null,
                    [Message.User("charge 10")], ["charge"]),
                new TrajectoryResponse(
                    [ToolUse("t9", "charge", """{"amount":10}""")], "tool_use", 10, 5)),
        ]);

        var (agent, ledger) = Replaying(uncovered, ToolReplayMode.FromRecording);

        var thrown = await Assert.ThrowsAsync<TrajectoryDivergenceException>(
            async () => await agent.RunAsync("charge 10"));

        await Assert.That(thrown!.Message).Contains("'charge' (t9) has no recorded result");
        await Assert.That(thrown.Message).Contains("ToolReplayMode.Execute");
        await Assert.That(ledger.Entries).IsEmpty();
    }

    [Test]
    public async Task An_error_result_replays_as_an_error()
    {
        // Record a run where the tool argument is invalid, so the recorded result is an error.
        var options = new AgentOptions { Model = "claude-test-1" };
        options.Tools.Add(SampleTools.AddTool);
        var transport = new FakeTransport();
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "add", """{"left":"one"}""")));
        transport.EnqueueTurn(FakeTransport.TextTurn("I mistyped that."));
        var recorder = new TrajectoryRecorder();
        await new ClaudeAgent(options, new RecordingTransport(transport, recorder)).RunAsync("add");

        var replayOptions = new AgentOptions { Model = "claude-test-1" };
        replayOptions.Tools.Add(SampleTools.AddTool);
        var replayed = await new ClaudeAgent(
            replayOptions, recorder.ToTrajectory(), ToolReplayMode.FromRecording).RunAsync("add");

        var result = replayed.Conversation.Messages
            .SelectMany(m => m.Content.OfType<ToolResultBlock>())
            .Single();
        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Content).Contains("must be a whole number");
    }

    [Test]
    public async Task Contracts_are_still_enforced_on_a_hermetic_replay()
    {
        // The cassette is consulted after the contract check, so a call the rules block stays
        // blocked rather than being answered out of the recording. Tightening a limit after the
        // fact is how you find out whether the rule would have held.
        var (trajectory, _) = await RecordAsync();

        var ledger = new Ledger();
        var options = new AgentOptions { Model = "claude-test-1" };
        options.Tools.Add(ledger.Tool);
        options.Rules.Limit("charge", maxCalls: 1);

        var result = await new ClaudeAgent(options, trajectory, ToolReplayMode.FromRecording)
            .RunAsync("charge 10 then 25");

        var results = result.Conversation.Messages
            .SelectMany(m => m.Content.OfType<ToolResultBlock>())
            .ToList();
        await Assert.That(results[0].Content).IsEqualTo("charged 10");
        await Assert.That(results[0].IsError).IsFalse();
        await Assert.That(results[1].IsError).IsTrue();
        await Assert.That(results[1].Content).Contains("exceeded its limit of 1 call(s)");
        await Assert.That(ledger.Entries).IsEmpty();
    }

    [Test]
    public async Task Constructing_a_replay_validates_its_arguments()
    {
        await Assert.That(() => new ClaudeAgent(new AgentOptions(), (Trajectory)null!))
            .Throws<ArgumentNullException>();
    }

    private static ToolUseBlock ToolUse(string id, string name, string inputJson)
    {
        using var document = System.Text.Json.JsonDocument.Parse(inputJson);
        return new ToolUseBlock(id, name, document.RootElement.Clone());
    }
}
