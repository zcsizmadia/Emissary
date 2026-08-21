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
