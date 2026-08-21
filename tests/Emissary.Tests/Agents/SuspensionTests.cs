using System.Text.Json;
using Emissary.Tests.Agents;
using Emissary.Tests.Tools;
using Emissary.Transport;

namespace Emissary.Tests;

public sealed class SuspensionTests
{
    private static (ClaudeAgent Agent, FakeTransport Transport) Create(Action<AgentOptions>? configure = null)
    {
        var options = new AgentOptions();
        options.Tools.Add(SampleTools.EchoTool);
        options.Tools.Add(SampleTools.SendPaymentTool);
        options.ApprovalRequired = tool => tool.Privileged;
        configure?.Invoke(options);
        var transport = new FakeTransport();
        return (new ClaudeAgent(options, transport), transport);
    }

    private static void EnqueueGatedBatch(FakeTransport transport)
    {
        transport.EnqueueTurn(FakeTransport.ToolTurn(
            FakeTransport.Use("t1", "echo", """{"text":"safe"}"""),
            FakeTransport.Use("t2", "send_payment", """{"amount":250}""")));
        transport.EnqueueTurn(FakeTransport.TextTurn("finished"));
    }

    [Test]
    public async Task Gated_tool_suspends_the_run_with_full_state()
    {
        var (agent, transport) = Create();
        EnqueueGatedBatch(transport);

        var events = new List<AgentEvent>();
        AgentResult? result = null;
        await foreach (var agentEvent in agent.StreamAsync("pay"))
        {
            events.Add(agentEvent);
            if (agentEvent is AgentCompletedEvent completed)
            {
                result = completed.Result;
            }
        }

        await Assert.That(result!.StopReason).IsEqualTo(AgentStopReason.AwaitingApproval);
        await Assert.That(events.OfType<AgentSuspendedEvent>().Count()).IsEqualTo(1);

        var suspension = result.Suspension!;
        await Assert.That(suspension.PendingApprovals.Single().ToolName).IsEqualTo("send_payment");
        await Assert.That(suspension.PendingApprovals.Single().Input.GetProperty("amount").GetInt32()).IsEqualTo(250);
        await Assert.That(suspension.CompletedResults.Single()).IsEqualTo(new ToolResultBlock("t1", "safe", false));
        await Assert.That(suspension.Messages[^1].Role).IsEqualTo(MessageRole.Assistant);
        await Assert.That(transport.Requests.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Resume_with_approval_executes_and_finishes()
    {
        var (agent, transport) = Create();
        EnqueueGatedBatch(transport);

        var suspended = await agent.RunAsync("pay");
        var result = await agent.ResumeAsync(suspended.Suspension!, approve: true);

        await Assert.That(result.StopReason).IsEqualTo(AgentStopReason.Completed);
        await Assert.That(result.FinalText).IsEqualTo("finished");

        // The follow-up request carries both results, ordered like the assistant's tool uses.
        var followUp = transport.Requests[1].Messages[^1].Content.Cast<ToolResultBlock>().ToArray();
        await Assert.That(followUp[0]).IsEqualTo(new ToolResultBlock("t1", "safe", false));
        await Assert.That(followUp[1]).IsEqualTo(new ToolResultBlock("t2", "sent 250", false));
    }

    [Test]
    public async Task Resume_survives_json_round_trip()
    {
        var (agent, transport) = Create();
        EnqueueGatedBatch(transport);

        var suspended = await agent.RunAsync("pay");
        var restored = SuspendedRun.FromJson(suspended.Suspension!.ToJson());
        var result = await agent.ResumeAsync(restored, approve: true);

        await Assert.That(result.FinalText).IsEqualTo("finished");
        await Assert.That(result.Conversation.Id.Value).IsEqualTo(suspended.Conversation.Id.Value);
    }

    [Test]
    public async Task Resume_with_denial_informs_the_model()
    {
        var (agent, transport) = Create();
        EnqueueGatedBatch(transport);

        var suspended = await agent.RunAsync("pay");
        var result = await agent.ResumeAsync(suspended.Suspension!, approve: false);

        await Assert.That(result.StopReason).IsEqualTo(AgentStopReason.Completed);
        var denial = (ToolResultBlock)transport.Requests[1].Messages[^1].Content[1];
        await Assert.That(denial.IsError).IsTrue();
        await Assert.That(denial.Content).Contains("Denied: a human reviewer rejected");
    }

    [Test]
    public async Task Guard_state_survives_suspension()
    {
        var (agent, transport) = Create(options =>
        {
            options.Tools.Add(SampleTools.ReadPageTool);
            options.ApprovalRequired = tool => tool.Name == "echo";
        });
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "read_page", """{"url":"evil"}""")));
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t2", "echo", """{"text":"gated"}""")));
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t3", "send_payment", """{"amount":1}""")));
        transport.EnqueueTurn(FakeTransport.TextTurn("done"));

        var suspended = await agent.RunAsync("go");
        await Assert.That(suspended.Suspension!.Guard.Tainted).IsTrue();

        var result = await agent.ResumeAsync(suspended.Suspension!, approve: true);

        // The taint from before the suspension still blocks the privileged tool after it.
        var blocked = (ToolResultBlock)transport.Requests[3].Messages[^1].Content.Single();
        await Assert.That(blocked.Content).Contains("cannot run after untrusted content");
        await Assert.That(result.Tainted).IsTrue();
    }

    [Test]
    public async Task Attempt_counts_survive_suspension()
    {
        var (agent, transport) = Create(options =>
        {
            options.Rules.Limit("send_payment", 1);
        });
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "send_payment", """{"amount":1}""")));
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t2", "send_payment", """{"amount":2}""")));
        transport.EnqueueTurn(FakeTransport.TextTurn("done"));

        var suspended = await agent.RunAsync("go");
        var result = await agent.ResumeAsync(suspended.Suspension!, approve: true);

        var second = (ToolResultBlock)transport.Requests[2].Messages[^1].Content.Single();
        await Assert.That(second.Content).Contains("exceeded its limit of 1 call(s)");
        _ = result;
    }

    [Test]
    public async Task Resume_with_unknown_pending_tool_reports_it()
    {
        var (agent, transport) = Create();
        transport.EnqueueTurn(FakeTransport.TextTurn("done"));

        using var document = JsonDocument.Parse("{}");
        var run = new SuspendedRun(
            Guid.CreateVersion7(),
            [Message.User("go"),
             new Message(MessageRole.Assistant, [new ToolUseBlock("t1", "ghost_tool", document.RootElement.Clone())])],
            AgentUsage.Zero,
            [],
            [new PlannedEffect("ghost_tool", "t1", document.RootElement.Clone())],
            new GuardSnapshot([], new Dictionary<string, int>(), null, false, null),
            []);

        await agent.ResumeAsync(run, approve: true);

        var reported = (ToolResultBlock)transport.Requests[0].Messages[^1].Content.Single();
        await Assert.That(reported.Content).IsEqualTo("Unknown tool 'ghost_tool'.");
    }

    [Test]
    public async Task Shadow_mode_wins_no_approval_gating()
    {
        var (agent, transport) = Create(options => options.Mode = ExecutionMode.Shadow);
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "send_payment", """{"amount":9}""")));
        transport.EnqueueTurn(FakeTransport.TextTurn("done"));

        var result = await agent.RunAsync("go");

        await Assert.That(result.StopReason).IsEqualTo(AgentStopReason.Completed);
        await Assert.That(result.PlannedEffects.Single().ToolName).IsEqualTo("send_payment");
        await Assert.That((object?)result.Suspension).IsNull();
    }

    [Test]
    public async Task Resume_validates_arguments()
    {
        var (agent, _) = Create();
        await Assert.That(async () => { await agent.ResumeAsync(null!, true); }).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task SuspendedRun_json_rejects_null_literal()
    {
        await Assert.That(() => SuspendedRun.FromJson("null")).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task In_memory_store_round_trips()
    {
        var store = new InMemoryAgentStateStore();
        using var document = JsonDocument.Parse("{}");
        var run = new SuspendedRun(
            Guid.CreateVersion7(), [Message.User("x")], AgentUsage.Zero, [],
            [new PlannedEffect("t", "id", document.RootElement.Clone())],
            new GuardSnapshot([], new Dictionary<string, int>(), null, false, null), []);

        await Assert.That(async () => { await store.SaveAsync(null!); }).Throws<ArgumentNullException>();
        await store.SaveAsync(run);
        await Assert.That((await store.LoadAsync(run.ConversationId))!.ConversationId).IsEqualTo(run.ConversationId);
        await store.DeleteAsync(run.ConversationId);
        await Assert.That(await store.LoadAsync(run.ConversationId)).IsNull();
    }

    [Test]
    public async Task Conversation_restore_validates()
    {
        await Assert.That(() => Conversation.Restore(ConversationId.New(), null!)).Throws<ArgumentNullException>();
    }
}
