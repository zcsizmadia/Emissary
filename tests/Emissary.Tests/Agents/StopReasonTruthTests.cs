using System.Collections.Immutable;
using Emissary.Tests.Agents;
using Emissary.Tests.Tools;
using Emissary.Transport;

namespace Emissary.Tests;

/// <summary>
/// The stop reason a run reports must be the one the API sent. These cases were unreachable in
/// production while every offline test passed, because the transport read the SDK enum's JSON
/// rendering and normalized it to end_turn.
/// </summary>
public sealed class StopReasonTruthTests
{
    private static async Task<AgentResult> RunAsync(StreamCompleted turn)
    {
        var options = new AgentOptions();
        options.Tools.Add(SampleTools.EchoTool);
        var transport = new FakeTransport();
        transport.EnqueueTurn(turn);
        return await new ClaudeAgent(options, transport).RunAsync("go");
    }

    [Test]
    public async Task A_truncated_answer_reports_max_tokens()
    {
        var result = await RunAsync(FakeTransport.TextTurn("this answer was cut off mid-", "max_tokens"));

        await Assert.That(result.StopReason).IsEqualTo(AgentStopReason.MaxTokens);
    }

    [Test]
    public async Task A_refusal_reports_refusal()
    {
        var result = await RunAsync(FakeTransport.TextTurn("I can't help with that.", "refusal"));

        await Assert.That(result.StopReason).IsEqualTo(AgentStopReason.Refusal);
    }

    [Test]
    public async Task A_paused_turn_is_not_reported_as_completed()
    {
        var result = await RunAsync(FakeTransport.TextTurn("Searching for that…", "pause_turn"));

        // The answer is incomplete; saying Completed would be a lie the caller cannot detect.
        await Assert.That(result.StopReason).IsEqualTo(AgentStopReason.Paused);
        await Assert.That(result.FinalText).IsEqualTo("Searching for that…");
    }

    [Test]
    public async Task A_turn_that_assembles_to_nothing_ends_the_run_without_an_empty_message()
    {
        // Every block was a kind the transport does not surface, so the turn has no content.
        var result = await RunAsync(new StreamCompleted(
            new ModelResponse(ImmutableArray<ContentBlock>.Empty, "end_turn", 10, 5)));

        await Assert.That(result.StopReason).IsEqualTo(AgentStopReason.Completed);

        // An empty assistant message would make the next request invalid, so it is never appended.
        await Assert.That(result.Conversation.Messages.Count).IsEqualTo(1);
        await Assert.That(result.Conversation.Messages[0].Role).IsEqualTo(MessageRole.User);
        await Assert.That(result.Usage.InputTokens).IsEqualTo(10);
    }

    [Test]
    public async Task An_empty_paused_turn_still_reports_paused()
    {
        var result = await RunAsync(new StreamCompleted(
            new ModelResponse(ImmutableArray<ContentBlock>.Empty, "pause_turn", 1, 1)));

        await Assert.That(result.StopReason).IsEqualTo(AgentStopReason.Paused);
    }

    [Test]
    public async Task A_paused_run_can_be_continued_by_running_the_conversation_again()
    {
        var options = new AgentOptions();
        var transport = new FakeTransport();
        transport.EnqueueTurn(FakeTransport.TextTurn("Let me look that up…", "pause_turn"));
        transport.EnqueueTurn(FakeTransport.TextTurn("It costs $30."));
        var agent = new ClaudeAgent(options, transport);

        var paused = await agent.RunAsync("how much is it?");
        await Assert.That(paused.StopReason).IsEqualTo(AgentStopReason.Paused);

        var continued = await agent.RunAsync(paused.Conversation);

        await Assert.That(continued.StopReason).IsEqualTo(AgentStopReason.Completed);
        await Assert.That(continued.FinalText).IsEqualTo("It costs $30.");
    }
}
