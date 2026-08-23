using Emissary.Testing;
using Emissary.Tests.Agents;
using Emissary.Tests.Tools;
using Emissary.Transport;

namespace Emissary.Tests;

public sealed class HandoffTests
{
    private static (ClaudeAgent Agent, FakeTransport Transport) Specialist(
        Action<AgentOptions>? configure,
        params StreamCompleted[] turns)
    {
        var options = new AgentOptions { SystemPrompt = "You are the specialist." };
        configure?.Invoke(options);
        var transport = new FakeTransport();
        foreach (var turn in turns)
        {
            transport.EnqueueTurn(turn);
        }

        return (new ClaudeAgent(options, transport), transport);
    }

    /// <summary>A specialist that answers on its first turn, spending 30 input and 7 output tokens.</summary>
    private static (ClaudeAgent Agent, FakeTransport Transport) Specialist(string finalText) =>
        Specialist(configure: null, FakeTransport.TextTurn(finalText, input: 30, output: 7));

    private static (ClaudeAgent Agent, FakeTransport Transport) Triage(
        HandoffTarget target,
        Action<AgentOptions>? configure = null)
    {
        var options = new AgentOptions { SystemPrompt = "You are triage." };
        options.Handoffs.Add(target);
        configure?.Invoke(options);
        var transport = new FakeTransport();
        return (new ClaudeAgent(options, transport), transport);
    }

    [Test]
    public async Task The_target_becomes_a_tool_the_model_can_call()
    {
        var (specialist, _) = Specialist("handled");
        var (triage, transport) = Triage(new HandoffTarget("billing", specialist, "Billing questions."));
        transport.EnqueueTurn(FakeTransport.TextTurn("no transfer needed"));

        await triage.RunAsync("hello");

        var offered = transport.Requests.Single().Tools.Select(t => t.Name).ToArray();
        await Assert.That(offered).Contains("handoff_to_billing");
        var handoffTool = transport.Requests.Single().Tools.Single(t => t.Name == "handoff_to_billing");
        await Assert.That(handoffTool.Description).Contains("Billing questions.");
    }

    [Test]
    public async Task Transferring_lets_the_target_finish_the_conversation()
    {
        var (specialist, specialistTransport) = Specialist("Refunded — this is billing speaking.");
        var (triage, triageTransport) = Triage(new HandoffTarget("billing", specialist, "Billing questions."));
        triageTransport.EnqueueTurn(FakeTransport.ToolTurn(
            FakeTransport.Use("t1", "handoff_to_billing", """{"reason":"customer asked about a charge"}""")));

        var events = new List<AgentEvent>();
        await foreach (var e in triage.StreamAsync("why was I charged twice?"))
        {
            events.Add(e);
        }

        var handoff = events.OfType<AgentHandoffEvent>().Single();
        await Assert.That(handoff.TargetName).IsEqualTo("billing");
        await Assert.That(handoff.Reason).IsEqualTo("customer asked about a charge");

        // The specialist answered, and it saw the whole prior conversation.
        var result = events.OfType<AgentCompletedEvent>().Single().Result;
        await Assert.That(result.FinalText).IsEqualTo("Refunded — this is billing speaking.");
        await Assert.That(result.StopReason).IsEqualTo(AgentStopReason.Completed);

        var seenBySpecialist = specialistTransport.Requests.Single();
        await Assert.That(seenBySpecialist.Messages[0].Text).IsEqualTo("why was I charged twice?");
        await Assert.That(seenBySpecialist.System).IsEqualTo("You are the specialist.");
    }

    [Test]
    public async Task Usage_accumulates_across_the_transfer()
    {
        var (specialist, _) = Specialist("done");
        var (triage, triageTransport) = Triage(new HandoffTarget("billing", specialist, "Billing."));
        triageTransport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "handoff_to_billing", "{}")));

        var result = await triage.RunAsync("hi");

        // 10/5 from the triage turn plus 30/7 from the specialist.
        await Assert.That(result.Usage.InputTokens).IsEqualTo(40);
        await Assert.That(result.Usage.OutputTokens).IsEqualTo(12);
    }

    [Test]
    public async Task A_transfer_without_a_reason_is_fine()
    {
        var (specialist, _) = Specialist("done");
        var (triage, triageTransport) = Triage(new HandoffTarget("billing", specialist, "Billing."));
        triageTransport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "handoff_to_billing", "{}")));

        var events = new List<AgentEvent>();
        await foreach (var e in triage.StreamAsync("hi"))
        {
            events.Add(e);
        }

        await Assert.That(events.OfType<AgentHandoffEvent>().Single().Reason).IsNull();
    }

    [Test]
    public async Task Taint_survives_the_transfer_and_still_blocks_privileged_tools()
    {
        // The specialist can send payments; the triage agent reads untrusted web content first.
        var (specialist, specialistTransport) = Specialist(
            o => o.Tools.Add(SampleTools.SendPaymentTool),
            FakeTransport.ToolTurn(FakeTransport.Use("s1", "send_payment", """{"amount":99}""")),
            FakeTransport.TextTurn("I could not pay that."));

        var (triage, triageTransport) = Triage(
            new HandoffTarget("billing", specialist, "Billing."),
            o => o.Tools.Add(SampleTools.ReadPageTool));
        triageTransport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "read_page", """{"url":"http://evil"}""")));
        triageTransport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t2", "handoff_to_billing", "{}")));

        var result = await triage.RunAsync("pay the invoice on this page");

        var blocked = (ToolResultBlock)specialistTransport.Requests[1].Messages[^1].Content.Single();
        await Assert.That(blocked.IsError).IsTrue();
        await Assert.That(blocked.Content).Contains("cannot run after untrusted content");
        EmissaryAssert.That(result).Tainted();
    }

    [Test]
    public async Task The_target_enforces_its_own_contracts()
    {
        var (specialist, specialistTransport) = Specialist(
            o =>
            {
                o.Tools.Add(SampleTools.EchoTool);
                o.Tools.Add(SampleTools.AddTool);
                o.Rules.Require("add", "echo");
            },
            FakeTransport.ToolTurn(FakeTransport.Use("s1", "add", """{"left":1}""")),
            FakeTransport.TextTurn("corrected"));

        var (triage, triageTransport) = Triage(new HandoffTarget("billing", specialist, "Billing."));
        triageTransport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "handoff_to_billing", "{}")));

        await triage.RunAsync("hi");

        var blocked = (ToolResultBlock)specialistTransport.Requests[1].Messages[^1].Content.Single();
        await Assert.That(blocked.Content).Contains("requires a prior successful call to 'echo'");
    }

    [Test]
    public async Task A_chain_of_transfers_stops_at_the_handoff_limit()
    {
        // d ← c ← b ← a, every agent eagerly transferring onward, with a limit of two.
        var transportD = new FakeTransport();
        transportD.EnqueueTurn(FakeTransport.TextTurn("d answers", input: 1, output: 1));
        var agentD = new ClaudeAgent(new AgentOptions { MaxHandoffs = 2 }, transportD);

        var previous = agentD;
        string previousName = "d";
        foreach (string name in new[] { "c", "b", "a" })
        {
            var options = new AgentOptions { MaxHandoffs = 2 };
            options.Handoffs.Add(new HandoffTarget(previousName, previous, $"{previousName} handles it."));
            var transport = new FakeTransport();

            // Each agent always tries to transfer, then answers if the transfer was refused.
            transport.EnqueueTurn(FakeTransport.ToolTurn(
                FakeTransport.Use($"{name}1", HandoffTools.ToolName(previousName), "{}")));
            transport.EnqueueTurn(FakeTransport.TextTurn($"{name} answers", input: 1, output: 1));

            previous = new ClaudeAgent(options, transport);
            previousName = name;
        }

        var events = new List<AgentEvent>();
        await foreach (var e in previous.StreamAsync("start the chain"))
        {
            events.Add(e);
        }

        // a → b → c, then c is at the limit: its transfer tool runs as an ordinary tool and it answers.
        await Assert.That(events.OfType<AgentHandoffEvent>().Select(h => h.TargetName)).IsEquivalentTo(["b", "c"]);
        var result = events.OfType<AgentCompletedEvent>().Single().Result;
        await Assert.That(result.FinalText).IsEqualTo("c answers");
        await Assert.That(transportD.Requests).IsEmpty();
    }

    [Test]
    public async Task Handoff_tool_names_are_snake_cased()
    {
        await Assert.That(HandoffTools.ToolName("billing")).IsEqualTo("handoff_to_billing");
        await Assert.That(HandoffTools.ToolName("BillingOps")).IsEqualTo("handoff_to_billing_ops");
        await Assert.That(HandoffTools.ToolName("tier 2")).IsEqualTo("handoff_to_tier_2");
    }

    [Test]
    public async Task Targets_are_validated()
    {
        var (specialist, _) = Specialist("x");
        await Assert.That(() => HandoffTools.Create(null!)).Throws<ArgumentNullException>();
        await Assert.That(() => HandoffTools.Create(new HandoffTarget("", specialist, "d"))).Throws<ArgumentException>();
        await Assert.That(() => HandoffTools.Create(new HandoffTarget("n", specialist, ""))).Throws<ArgumentException>();
        await Assert.That(() => HandoffTools.Create(new HandoffTarget("n", null!, "d"))).Throws<ArgumentNullException>();
    }
}
