using Emissary.Testing;
using Emissary.Tests.Agents;
using Emissary.Tests.Tools;

namespace Emissary.Tests;

public sealed class ShadowAndCompensationTests
{
    private static (ClaudeAgent Agent, FakeTransport Transport, AgentOptions Options) Create(Action<AgentOptions> configure)
    {
        var options = new AgentOptions();
        options.Tools.Add(SampleTools.EchoTool);
        options.Tools.Add(SampleTools.SendPaymentTool);
        configure(options);
        var transport = new FakeTransport();
        return (new ClaudeAgent(options, transport), transport, options);
    }

    private static ToolResultBlock Result(FakeTransport transport, int requestIndex, int blockIndex = 0) =>
        (ToolResultBlock)transport.Requests[requestIndex].Messages[^1].Content[blockIndex];

    [Test]
    public async Task Shadow_mode_intercepts_privileged_tools_and_plans_effects()
    {
        var (agent, transport, _) = Create(options => options.Mode = ExecutionMode.Shadow);
        transport.EnqueueTurn(FakeTransport.ToolTurn(
            FakeTransport.Use("t1", "send_payment", """{"amount":75.5}"""),
            FakeTransport.Use("t2", "echo", """{"text":"still runs"}""")));
        transport.EnqueueTurn(FakeTransport.TextTurn("queued"));

        var result = await agent.RunAsync("go");

        var intercepted = Result(transport, 1, 0);
        await Assert.That(intercepted.IsError).IsFalse();
        await Assert.That(intercepted.Content).Contains("[shadow]");
        await Assert.That(Result(transport, 1, 1).Content).IsEqualTo("still runs");

        var effect = result.PlannedEffects.Single();
        await Assert.That(effect.ToolName).IsEqualTo("send_payment");
        await Assert.That(effect.ToolUseId).IsEqualTo("t1");
        await Assert.That(effect.Input.GetProperty("amount").GetDouble()).IsEqualTo(75.5);
        EmissaryAssert.That(result).EffectPlanned("send_payment");
    }

    [Test]
    public async Task Live_mode_plans_nothing()
    {
        var (agent, transport, _) = Create(_ => { });
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "send_payment", """{"amount":5}""")));
        transport.EnqueueTurn(FakeTransport.TextTurn("done"));

        var result = await agent.RunAsync("go");

        await Assert.That(Result(transport, 1).Content).IsEqualTo("sent 5");
        EmissaryAssert.That(result).NoPlannedEffects();
    }

    [Test]
    public async Task Taint_block_wins_over_shadow_interception()
    {
        var (agent, transport, _) = Create(options =>
        {
            options.Mode = ExecutionMode.Shadow;
            options.Tools.Add(SampleTools.ReadPageTool);
        });
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "read_page", """{"url":"evil"}""")));
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t2", "send_payment", """{"amount":9}""")));
        transport.EnqueueTurn(FakeTransport.TextTurn("done"));

        var result = await agent.RunAsync("go");

        await Assert.That(Result(transport, 2).IsError).IsTrue();
        await Assert.That(Result(transport, 2).Content).Contains("privileged tool 'send_payment' cannot run");
        EmissaryAssert.That(result).Tainted().NoPlannedEffects();
    }

    [Test]
    [NotInParallel]
    public async Task Compensation_unwinds_successful_calls_in_reverse_order()
    {
        SampleTools.BookingLog.Clear();
        var (agent, transport, _) = Create(options => options.Tools.Add(SampleTools.BookRoomTool));
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "book_room", """{"room":"A"}""")));
        transport.EnqueueTurn(FakeTransport.ToolTurn(
            FakeTransport.Use("t2", "book_room", """{"room":"B"}"""),
            FakeTransport.Use("t3", "echo", """{"text":"no compensation"}""")));
        transport.EnqueueTurn(FakeTransport.TextTurn("booked"));

        var result = await agent.RunAsync("go");
        var report = await agent.CompensateAsync(result);

        await Assert.That(report.Count).IsEqualTo(2);
        await Assert.That(report[0]).IsEqualTo(new CompensationResult("book_room", "t2", true, "cancelled B"));
        await Assert.That(report[1]).IsEqualTo(new CompensationResult("book_room", "t1", true, "cancelled A"));
        await Assert.That(SampleTools.BookingLog)
            .IsEquivalentTo(["booked A", "booked B", "cancelled B", "cancelled A"]);
    }

    [Test]
    public async Task Compensation_skips_failed_calls_and_shadowed_effects()
    {
        var (agent, transport, _) = Create(options =>
        {
            options.Tools.Add(SampleTools.BookRoomTool);
            options.Mode = ExecutionMode.Shadow;
        });
        transport.EnqueueTurn(FakeTransport.ToolTurn(
            FakeTransport.Use("t1", "book_room", "{}"),
            FakeTransport.Use("t2", "send_payment", """{"amount":1}""")));
        transport.EnqueueTurn(FakeTransport.TextTurn("done"));

        var result = await agent.RunAsync("go");
        var report = await agent.CompensateAsync(result);

        // book_room failed (missing argument) and send_payment was only shadow-planned -
        // neither may be compensated.
        await Assert.That(report.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Compensation_failure_is_reported_not_thrown()
    {
        var (agent, _, _) = Create(options => options.Tools.Add(SampleTools.BookRoomTool));

        // A conversation whose recorded tool input lacks the member the compensator needs.
        var result = new AgentResult
        {
            Conversation = Conversation.Start()
                .Append(Message.User("go"))
                .Append(new Message(MessageRole.Assistant, [FakeTransport.Use("t1", "book_room", "{}")]))
                .Append(new Message(MessageRole.User, [new ToolResultBlock("t1", "booked", false)])),
            StopReason = AgentStopReason.Completed,
            Usage = AgentUsage.Zero,
        };

        var report = await agent.CompensateAsync(result);

        await Assert.That(report.Single().Success).IsFalse();
        await Assert.That(report.Single().Output).Contains("missing required argument 'room'");
    }

    [Test]
    public async Task Compensate_validates_input()
    {
        var (agent, _, _) = Create(_ => { });
        await Assert.That(async () => { await agent.CompensateAsync(null!); }).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Generated_compensation_metadata_is_wired()
    {
        await Assert.That((object?)SampleTools.BookRoomTool.Compensation).IsNotNull();
        await Assert.That((object?)SampleTools.CancelRoomTool.Compensation).IsNull();
        await Assert.That((object?)SampleTools.EchoTool.Compensation).IsNull();
    }

    [Test]
    public async Task Effect_assertions_fail_in_both_directions()
    {
        var (agent, transport, _) = Create(_ => { });
        transport.EnqueueTurn(FakeTransport.TextTurn("plain"));
        var clean = await agent.RunAsync("go");

        await Assert.That(() => EmissaryAssert.That(clean).EffectPlanned("send_payment"))
            .Throws<EmissaryAssertionException>();

        var (shadowAgent, shadowTransport, _) = Create(options => options.Mode = ExecutionMode.Shadow);
        shadowTransport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "send_payment", """{"amount":1}""")));
        shadowTransport.EnqueueTurn(FakeTransport.TextTurn("done"));
        var shadow = await shadowAgent.RunAsync("go");

        await Assert.That(() => EmissaryAssert.That(shadow).NoPlannedEffects())
            .Throws<EmissaryAssertionException>();
    }
}
