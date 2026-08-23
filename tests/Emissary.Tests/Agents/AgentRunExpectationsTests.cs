using System.Text.Json;
using Emissary.Testing;

namespace Emissary.Tests;

public sealed class AgentRunExpectationsTests
{
    private static ToolUseBlock Use(string id, string name)
    {
        using var document = JsonDocument.Parse("{}");
        return new ToolUseBlock(id, name, document.RootElement.Clone());
    }

    private static AgentResult Run(params string[] toolNamesInOrder)
    {
        var conversation = Conversation.Start().Append(Message.User("go"));
        int id = 0;
        foreach (string name in toolNamesInOrder)
        {
            conversation = conversation
                .Append(new Message(MessageRole.Assistant, [Use($"t{id++}", name)]))
                .Append(new Message(MessageRole.User, [new ToolResultBlock($"t{id - 1}", "ok", false)]));
        }

        conversation = conversation.Append(new Message(MessageRole.Assistant, [new TextBlock("all done")]));
        return new AgentResult
        {
            Conversation = conversation,
            StopReason = AgentStopReason.Completed,
            Usage = AgentUsage.Zero,
        };
    }

    private static AgentResult WithFailures(params ToolFailure[] failures) => new()
    {
        Conversation = Conversation.Start().Append(Message.User("go")),
        StopReason = AgentStopReason.Completed,
        Usage = AgentUsage.Zero,
        ToolFailures = failures,
    };

    [Test]
    public async Task Tool_failure_expectations_pass_and_fail_with_useful_messages()
    {
        var clean = Run("echo");
        var failed = WithFailures(
            new ToolFailure("t1", "charge_card", new TimeoutException("gone"), TimedOut: false),
            new ToolFailure("t2", "slow_report", new TaskCanceledException(), TimedOut: true));

        // Passing.
        await Assert.That(EmissaryAssert.That(clean).NoToolFailures()).IsNotNull();
        await Assert.That(EmissaryAssert.That(failed).ToolFailed("charge_card").ToolTimedOut("slow_report"))
            .IsNotNull();

        // Failing, with the actual failures named so the message is actionable.
        var thrown = Assert.Throws<EmissaryAssertionException>(() => EmissaryAssert.That(failed).NoToolFailures());
        await Assert.That(thrown!.Message).Contains("charge_card: TimeoutException");
        await Assert.That(thrown.Message).Contains("slow_report: TaskCanceledException (timed out)");

        await Assert.That(Assert.Throws<EmissaryAssertionException>(
                () => EmissaryAssert.That(clean).ToolFailed("charge_card"))!.Message)
            .Contains("(none)");

        // A tool that threw did not time out, and vice versa.
        await Assert.That(Assert.Throws<EmissaryAssertionException>(
                () => EmissaryAssert.That(failed).ToolTimedOut("charge_card"))!.Message)
            .Contains("to have timed out");
    }

    [Test]
    [Arguments(AgentStopReason.MaxTokens)]
    [Arguments(AgentStopReason.Refusal)]
    [Arguments(AgentStopReason.Paused)]
    [Arguments(AgentStopReason.TurnLimit)]
    [Arguments(AgentStopReason.BudgetExceeded)]
    [Arguments(AgentStopReason.AwaitingApproval)]
    public async Task Complete_rejects_every_reason_that_cuts_the_answer_short(AgentStopReason stopReason)
    {
        var result = new AgentResult
        {
            Conversation = Conversation.Start().Append(Message.User("go")),
            StopReason = stopReason,
            Usage = AgentUsage.Zero,
        };

        var thrown = Assert.Throws<EmissaryAssertionException>(() => EmissaryAssert.That(result).Complete());

        await Assert.That(thrown!.Message).Contains(stopReason.ToString());
        await Assert.That(thrown.Message).Contains("not the whole answer");
    }

    [Test]
    public async Task Complete_passes_for_a_finished_run()
    {
        await Assert.That(EmissaryAssert.That(Run("echo")).Complete()).IsNotNull();
    }

    [Test]
    public async Task Passing_expectations_chain_fluently()
    {
        var result = Run("verify_identity", "refund_payment");

        var chained = EmissaryAssert.That(result)
            .ToolCalled("verify_identity")
            .ToolCalled("refund_payment", times: 1)
            .ToolNotCalled("close_ticket")
            .ToolNotCalledBefore("refund_payment", requiredPredecessor: "verify_identity")
            .ToolNotCalledBefore("close_ticket", requiredPredecessor: "verify_identity")
            .Stopped(AgentStopReason.Completed)
            .FinalTextContains("done");

        await Assert.That(chained).IsNotNull();
    }

    [Test]
    public async Task ToolCalled_fails_when_never_called()
    {
        await Assert.That(() => EmissaryAssert.That(Run()).ToolCalled("refund_payment"))
            .Throws<EmissaryAssertionException>()
            .WithMessageContaining("(none)");
    }

    [Test]
    public async Task ToolCalled_with_count_fails_on_mismatch()
    {
        await Assert.That(() => EmissaryAssert.That(Run("echo", "echo")).ToolCalled("echo", times: 1))
            .Throws<EmissaryAssertionException>()
            .WithMessageContaining("called 2 time(s)");
    }

    [Test]
    public async Task ToolNotCalled_fails_when_called()
    {
        await Assert.That(() => EmissaryAssert.That(Run("echo")).ToolNotCalled("echo"))
            .Throws<EmissaryAssertionException>();
    }

    [Test]
    public async Task ToolNotCalledBefore_fails_when_predecessor_missing()
    {
        await Assert.That(() => EmissaryAssert.That(Run("refund_payment"))
                .ToolNotCalledBefore("refund_payment", "verify_identity"))
            .Throws<EmissaryAssertionException>()
            .WithMessageContaining("without");
    }

    [Test]
    public async Task ToolNotCalledBefore_fails_when_order_is_wrong()
    {
        await Assert.That(() => EmissaryAssert.That(Run("refund_payment", "verify_identity"))
                .ToolNotCalledBefore("refund_payment", "verify_identity"))
            .Throws<EmissaryAssertionException>()
            .WithMessageContaining("before");
    }

    [Test]
    public async Task Stopped_fails_on_wrong_reason()
    {
        await Assert.That(() => EmissaryAssert.That(Run()).Stopped(AgentStopReason.Refusal))
            .Throws<EmissaryAssertionException>();
    }

    [Test]
    public async Task FinalTextContains_fails_on_missing_fragment()
    {
        await Assert.That(() => EmissaryAssert.That(Run()).FinalTextContains("absent"))
            .Throws<EmissaryAssertionException>();
    }

    [Test]
    public async Task Null_result_throws()
    {
        await Assert.That(() => EmissaryAssert.That(null!)).Throws<ArgumentNullException>();
    }
}
