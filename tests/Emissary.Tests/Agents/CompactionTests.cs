using Emissary.Tests.Agents;
using Emissary.Tests.Tools;
using Emissary.Transport;

namespace Emissary.Tests;

public sealed class CompactionTests
{
    private static Message Assistant(string text) => new(MessageRole.Assistant, [new TextBlock(text)]);

    private static List<Message> Chat(int pairs)
    {
        var messages = new List<Message>();
        for (int i = 0; i < pairs; i++)
        {
            messages.Add(Message.User($"user {i}"));
            messages.Add(Assistant($"reply {i}"));
        }

        return messages;
    }

    [Test]
    public async Task Cut_index_lands_on_an_assistant_message()
    {
        var messages = Chat(6); // 12 messages: user/assistant alternating

        int? cut = ConversationCompactor.TryFindCutIndex(messages, keepRecentMessages: 5);

        await Assert.That(cut).IsNotNull();
        await Assert.That(messages[cut!.Value].Role).IsEqualTo(MessageRole.Assistant);
    }

    [Test]
    public async Task Cut_index_keeps_tool_pairs_intact()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("{}");
        var messages = new List<Message>
        {
            Message.User("start"),
            Assistant("thinking"),
            Message.User("go"),
            new(MessageRole.Assistant, [new ToolUseBlock("t1", "echo", doc.RootElement.Clone())]),
            new(MessageRole.User, [new ToolResultBlock("t1", "ok", false)]),
            Assistant("done"),
        };

        // Asking to keep 3 would land mid-pair; the compactor moves the boundary to the
        // assistant tool-use message so its results stay with it.
        int? cut = ConversationCompactor.TryFindCutIndex(messages, keepRecentMessages: 3);

        await Assert.That(cut).IsEqualTo(3);
        await Assert.That(messages[3].Content.OfType<ToolUseBlock>().Any()).IsTrue();
    }

    [Test]
    public async Task Short_conversations_are_not_compacted()
    {
        await Assert.That(ConversationCompactor.TryFindCutIndex([Message.User("only")], 6)).IsNull();
        await Assert.That(ConversationCompactor.TryFindCutIndex([Message.User("u"), Assistant("a")], 6)).IsNull();
    }

    [Test]
    public async Task No_assistant_message_in_range_means_no_compaction()
    {
        List<Message> allUser = [Message.User("a"), Message.User("b"), Message.User("c")];

        await Assert.That(ConversationCompactor.TryFindCutIndex(allUser, 1)).IsNull();
    }

    [Test]
    public async Task Summary_prompt_renders_text_tools_and_results()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("{}");
        var messages = new List<Message>
        {
            Message.User("hello"),
            new(MessageRole.Assistant, [new ToolUseBlock("t1", "echo", doc.RootElement.Clone())]),
            new(MessageRole.User, [new ToolResultBlock("t1", "pong", false)]),
            new(MessageRole.User, [new ToolResultBlock("t2", "boom", true)]),
            new(MessageRole.Assistant, [new ThinkingBlock("hmm", "sig"), new TextBlock("bye")]),
        };

        string prompt = ConversationCompactor.BuildSummaryPrompt(messages, 5, "INSTRUCTION");

        await Assert.That(prompt).StartsWith("INSTRUCTION");
        await Assert.That(prompt).Contains("User: hello");
        await Assert.That(prompt).Contains("[called echo]");
        await Assert.That(prompt).Contains("[tool result: pong]");
        await Assert.That(prompt).Contains("[tool error: boom]");
        await Assert.That(prompt).Contains("Assistant:  bye");
    }

    [Test]
    public async Task Apply_replaces_the_prefix_and_keeps_the_id()
    {
        var conversation = Conversation.Restore(ConversationId.New(), Chat(4));

        var compacted = ConversationCompactor.Apply(conversation, 5, "SUMMARY");

        await Assert.That(compacted.Id).IsEqualTo(conversation.Id);
        await Assert.That(compacted.Messages.Count).IsEqualTo(4); // summary + 3 kept
        await Assert.That(compacted.Messages[0].Role).IsEqualTo(MessageRole.User);
        await Assert.That(compacted.Messages[0].Text).Contains("SUMMARY");
        await Assert.That(compacted.Messages[1].Role).IsEqualTo(MessageRole.Assistant);
    }

    [Test]
    public async Task Agent_compacts_when_the_trigger_is_exceeded()
    {
        var options = new AgentOptions { Tools = { SampleTools.EchoTool } };
        options.Compaction.TriggerInputTokens = 100;
        options.Compaction.KeepRecentMessages = 2;
        var transport = new FakeTransport();

        // Turn 1: a big-input tool turn trips the trigger.
        transport.EnqueueTurn(new StreamCompleted(new ModelResponse(
            [new ToolUseBlock("t1", "echo", System.Text.Json.JsonDocument.Parse("""{"text":"a"}""").RootElement.Clone())],
            "tool_use", 500, 10)));
        // Turn 2: the compaction call.
        transport.EnqueueTurn(FakeTransport.TextTurn("SUMMARY OF EARLIER", input: 20, output: 5));
        // Turn 3: the real follow-up.
        transport.EnqueueTurn(FakeTransport.TextTurn("final answer", input: 30, output: 8));
        var agent = new ClaudeAgent(options, transport);

        // A conversation with real history — compaction only pays off past a couple of messages.
        var history = Conversation.Restore(ConversationId.New(), Chat(3));
        var events = new List<AgentEvent>();
        await foreach (var agentEvent in agent.StreamAsync(history.Append(Message.User("start"))))
        {
            events.Add(agentEvent);
        }

        var compacted = events.OfType<AgentCompactedEvent>().Single();
        await Assert.That(compacted.Summary).IsEqualTo("SUMMARY OF EARLIER");
        await Assert.That(compacted.MessagesSummarized).IsGreaterThan(0);

        // The compaction request was tool-free; the follow-up carried the summary.
        await Assert.That(transport.Requests[1].Tools.Count).IsEqualTo(0);
        await Assert.That(transport.Requests[2].Messages[0].Text).Contains("SUMMARY OF EARLIER");

        var result = events.OfType<AgentCompletedEvent>().Single().Result;
        await Assert.That(result.FinalText).IsEqualTo("final answer");
    }

    [Test]
    public async Task Compaction_is_off_by_default()
    {
        var transport = new FakeTransport();
        transport.EnqueueTurn(FakeTransport.TextTurn("done", input: 999_999, output: 5));
        var agent = new ClaudeAgent(new AgentOptions(), transport);

        var events = new List<AgentEvent>();
        await foreach (var agentEvent in agent.StreamAsync("go"))
        {
            events.Add(agentEvent);
        }

        await Assert.That(events.OfType<AgentCompactedEvent>().Any()).IsFalse();
        await Assert.That(transport.Requests.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Trigger_with_nothing_safe_to_compact_is_a_no_op()
    {
        var options = new AgentOptions { Tools = { SampleTools.EchoTool } };
        options.Compaction.TriggerInputTokens = 10;
        var transport = new FakeTransport();

        // A big-input tool turn trips the trigger and keeps the loop going...
        transport.EnqueueTurn(new StreamCompleted(new ModelResponse(
            [new ToolUseBlock("t1", "echo", System.Text.Json.JsonDocument.Parse("""{"text":"a"}""").RootElement.Clone())],
            "tool_use", 500, 5)));
        // ...but the conversation is still too short to be worth compacting, so the run just proceeds.
        transport.EnqueueTurn(FakeTransport.TextTurn("second", input: 20, output: 5));
        var agent = new ClaudeAgent(options, transport);

        var events = new List<AgentEvent>();
        await foreach (var agentEvent in agent.StreamAsync("go"))
        {
            events.Add(agentEvent);
        }

        await Assert.That(events.OfType<AgentCompactedEvent>().Any()).IsFalse();
        await Assert.That(events.OfType<AgentCompletedEvent>().Single().Result.FinalText).IsEqualTo("second");
    }

    [Test]
    public async Task Compaction_stream_without_completion_is_a_contract_violation()
    {
        var options = new AgentOptions { MaxTurns = 3, Tools = { SampleTools.EchoTool } };
        options.Compaction.TriggerInputTokens = 10;
        options.Compaction.KeepRecentMessages = 2;
        var transport = new FakeTransport();
        // A big-input tool turn keeps the loop going and trips the trigger...
        transport.EnqueueTurn(new StreamCompleted(new ModelResponse(
            [new ToolUseBlock("t1", "echo", System.Text.Json.JsonDocument.Parse("""{"text":"a"}""").RootElement.Clone())],
            "tool_use", 500, 5)));
        // ...then the compaction call yields no StreamCompleted, which is a contract violation.
        transport.EnqueueTurn(new StreamTextDelta("no completion event"));
        var agent = new ClaudeAgent(options, transport);

        var conversation = Conversation.Restore(ConversationId.New(), Chat(3)).Append(Message.User("c"));

        await Assert.That(async () => { await agent.RunAsync(conversation); })
            .Throws<InvalidOperationException>();
    }
}
