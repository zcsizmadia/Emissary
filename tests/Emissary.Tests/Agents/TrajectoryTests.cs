using Emissary.Tests.Agents;
using Emissary.Tests.Tools;
using Emissary.Transport;

namespace Emissary.Tests;

public sealed class TrajectoryTests
{
    private static AgentOptions EchoOptions() =>
        new() { SystemPrompt = "Use tools.", Tools = { SampleTools.EchoTool } };

    private static FakeTransport ToolLoopFake()
    {
        var fake = new FakeTransport();
        fake.EnqueueTurn(
            new StreamToolUseStart("t1", "echo"),
            FakeTransport.ToolTurn(FakeTransport.Use("t1", "echo", """{"text":"ping"}""")));
        fake.EnqueueTurn(
            new StreamThinkingDelta("hmm"),
            new StreamTextDelta("done"),
            new StreamCompleted(new ModelResponse(
                [new ThinkingBlock("hmm", "sig"), new RedactedThinkingBlock("opaque"), new TextBlock("done")],
                "end_turn", 20, 9)));
        return fake;
    }

    private static async Task<Trajectory> RecordToolLoop(AgentOptions options)
    {
        var recorder = new TrajectoryRecorder();
        var agent = new ClaudeAgent(options, new RecordingTransport(ToolLoopFake(), recorder));
        await agent.RunAsync("go");
        return recorder.ToTrajectory();
    }

    [Test]
    public async Task Recording_captures_every_turn()
    {
        var trajectory = await RecordToolLoop(EchoOptions());

        await Assert.That(trajectory.Version).IsEqualTo(Trajectory.CurrentVersion);
        await Assert.That(trajectory.Turns.Count).IsEqualTo(2);
        await Assert.That(trajectory.Turns[0].Request.ToolNames.Single()).IsEqualTo("echo");
        await Assert.That(trajectory.Turns[0].Response.StopReason).IsEqualTo("tool_use");
        await Assert.That(trajectory.Turns[1].Request.Messages.Count).IsEqualTo(3);
        await Assert.That(trajectory.Turns[1].Response.StopReason).IsEqualTo("end_turn");
    }

    [Test]
    public async Task Recording_skips_streams_that_never_complete()
    {
        var recorder = new TrajectoryRecorder();
        var fake = new FakeTransport();
        fake.EnqueueTurn(new StreamTextDelta("broken"));
        var agent = new ClaudeAgent(EchoOptions(), new RecordingTransport(fake, recorder));

        await Assert.That(async () => { await agent.RunAsync("go"); }).Throws<InvalidOperationException>();
        await Assert.That(recorder.ToTrajectory().Turns.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Json_round_trips_exactly()
    {
        var trajectory = await RecordToolLoop(EchoOptions());

        string json = trajectory.ToJson();
        string roundTripped = Trajectory.FromJson(json).ToJson();

        await Assert.That(roundTripped).IsEqualTo(json);
    }

    [Test]
    public async Task FromJson_rejects_null_literal()
    {
        await Assert.That(() => Trajectory.FromJson("null")).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Save_and_load_round_trip_through_a_file()
    {
        var trajectory = await RecordToolLoop(EchoOptions());
        string path = Path.Combine(Path.GetTempPath(), $"emissary-{Guid.CreateVersion7():N}.trajectory");

        try
        {
            trajectory.Save(path);
            var loaded = Trajectory.Load(path);
            await Assert.That(loaded.ToJson()).IsEqualTo(trajectory.ToJson());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Replay_reproduces_the_run_without_a_transport()
    {
        var options = EchoOptions();
        var trajectory = await RecordToolLoop(options);

        var replayAgent = new ClaudeAgent(EchoOptions(), trajectory);
        var events = new List<AgentEvent>();
        AgentResult? result = null;
        await foreach (var agentEvent in replayAgent.StreamAsync("go"))
        {
            events.Add(agentEvent);
            if (agentEvent is AgentCompletedEvent completed)
            {
                result = completed.Result;
            }
        }

        await Assert.That(result!.FinalText).IsEqualTo("done");
        await Assert.That(result.Usage).IsEqualTo(new AgentUsage(30, 14));
        await Assert.That(result.Conversation.Messages.Count).IsEqualTo(4);
        await Assert.That(events.OfType<AgentToolCallEvent>().Single().Name).IsEqualTo("echo");
        await Assert.That(events.OfType<AgentToolResultEvent>().Single().Result).IsEqualTo("ping");
        await Assert.That(events.OfType<AgentThinkingEvent>().Single().Delta).IsEqualTo("hmm");
        await Assert.That(events.OfType<AgentTextEvent>().Single().Delta).IsEqualTo("done");
    }

    [Test]
    public async Task Replay_diverges_on_extra_model_calls()
    {
        var trajectory = await RecordToolLoop(EchoOptions());
        var replayAgent = new ClaudeAgent(EchoOptions(), trajectory);

        await replayAgent.RunAsync("go");
        await Assert.That(async () => { await replayAgent.RunAsync("go"); })
            .Throws<TrajectoryDivergenceException>()
            .WithMessageContaining("more model calls");
    }

    [Test]
    public async Task Replay_diverges_on_model_change()
    {
        var trajectory = await RecordToolLoop(EchoOptions());
        var options = EchoOptions();
        options.Model = "claude-haiku-4-5";
        var replayAgent = new ClaudeAgent(options, trajectory);

        await Assert.That(async () => { await replayAgent.RunAsync("go"); })
            .Throws<TrajectoryDivergenceException>()
            .WithMessageContaining("Model diverged");
    }

    [Test]
    public async Task Replay_diverges_on_conversation_shape_change()
    {
        var trajectory = await RecordToolLoop(EchoOptions());
        var replayAgent = new ClaudeAgent(EchoOptions(), trajectory);
        var conversation = Conversation.Start()
            .Append(Message.User("one"))
            .Append(new Message(MessageRole.Assistant, [new TextBlock("two")]))
            .Append(Message.User("three"));

        await Assert.That(async () => { await replayAgent.RunAsync(conversation); })
            .Throws<TrajectoryDivergenceException>()
            .WithMessageContaining("Conversation shape diverged");
    }

    [Test]
    public async Task Replay_diverges_on_tool_change()
    {
        var trajectory = await RecordToolLoop(EchoOptions());
        var options = EchoOptions();
        options.Tools.Add(SampleTools.AddTool);
        var replayAgent = new ClaudeAgent(options, trajectory);

        await Assert.That(async () => { await replayAgent.RunAsync("go"); })
            .Throws<TrajectoryDivergenceException>()
            .WithMessageContaining("Tools diverged");
    }

    [Test]
    public async Task Recording_and_replay_constructors_validate()
    {
        _ = new ClaudeAgent(EchoOptions(), new TrajectoryRecorder());

        await Assert.That(() => new ClaudeAgent(EchoOptions(), (TrajectoryRecorder)null!))
            .Throws<ArgumentNullException>();
        await Assert.That(() => new ClaudeAgent(EchoOptions(), (Trajectory)null!))
            .Throws<ArgumentNullException>();
        await Assert.That(() => new ClaudeAgent(null!, new TrajectoryRecorder()))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Abandoning_a_recorded_stream_skips_the_incomplete_turn()
    {
        var recorder = new TrajectoryRecorder();
        var agent = new ClaudeAgent(EchoOptions(), new RecordingTransport(ToolLoopFake(), recorder));

        await foreach (var agentEvent in agent.StreamAsync("go"))
        {
            if (agentEvent is AgentToolCallEvent)
            {
                break;
            }
        }

        await Assert.That(recorder.ToTrajectory().Turns.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Abandoning_a_replayed_stream_disposes_cleanly()
    {
        var trajectory = await RecordToolLoop(EchoOptions());
        var replayAgent = new ClaudeAgent(EchoOptions(), trajectory);

        AgentEvent? first = null;
        await foreach (var agentEvent in replayAgent.StreamAsync("go"))
        {
            first = agentEvent;
            break;
        }

        await Assert.That(first).IsTypeOf<AgentToolCallEvent>();
    }
}
