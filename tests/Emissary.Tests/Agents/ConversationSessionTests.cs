using Emissary.Tests.Agents;
using Emissary.Tests.Tools;

namespace Emissary.Tests;

public sealed class ConversationSessionTests
{
    private static (ClaudeAgent Agent, FakeTransport Transport) Agent()
    {
        var transport = new FakeTransport();
        return (new ClaudeAgent(new AgentOptions(), transport), transport);
    }

    [Test]
    public async Task Session_persists_history_across_turns()
    {
        var (agent, transport) = Agent();
        transport.EnqueueTurn(FakeTransport.TextTurn("first reply"));
        transport.EnqueueTurn(FakeTransport.TextTurn("second reply"));
        var store = new InMemoryConversationStore();
        var session = new ConversationSession(agent, store, ConversationId.New());

        await session.SendAsync("hello");
        await session.SendAsync("again");

        // The second request carried the full prior history: user, assistant, user.
        var secondRequest = transport.Requests[1];
        await Assert.That(secondRequest.Messages.Count).IsEqualTo(3);
        await Assert.That(secondRequest.Messages[0].Text).IsEqualTo("hello");
        await Assert.That(secondRequest.Messages[1].Text).IsEqualTo("first reply");
        await Assert.That(secondRequest.Messages[2].Text).IsEqualTo("again");

        var stored = await store.LoadAsync(session.Id);
        await Assert.That(stored!.Messages.Count).IsEqualTo(4);
    }

    [Test]
    public async Task A_new_session_id_starts_empty()
    {
        var (agent, transport) = Agent();
        transport.EnqueueTurn(FakeTransport.TextTurn("hi"));
        var session = new ConversationSession(agent, new InMemoryConversationStore(), ConversationId.New());

        var loaded = await session.LoadOrStartAsync();
        await Assert.That(loaded.Messages.Count).IsEqualTo(0);
        await Assert.That(loaded.Id).IsEqualTo(session.Id);

        var result = await session.SendAsync("start");
        await Assert.That(result.FinalText).IsEqualTo("hi");
    }

    [Test]
    public async Task Two_sessions_resume_independently_by_id()
    {
        var store = new InMemoryConversationStore();
        var id1 = ConversationId.New();
        var id2 = ConversationId.New();

        var (a1, t1) = Agent();
        t1.EnqueueTurn(FakeTransport.TextTurn("a"));
        await new ConversationSession(a1, store, id1).SendAsync("to one");

        var (a2, t2) = Agent();
        t2.EnqueueTurn(FakeTransport.TextTurn("b"));
        await new ConversationSession(a2, store, id2).SendAsync("to two");

        await Assert.That((await store.LoadAsync(id1))!.Messages[0].Text).IsEqualTo("to one");
        await Assert.That((await store.LoadAsync(id2))!.Messages[0].Text).IsEqualTo("to two");
    }

    [Test]
    public async Task Conversation_json_round_trips()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""{"x":1}""");
        var conversation = Conversation.Restore(ConversationId.New(),
        [
            Message.User("q"),
            new Message(MessageRole.Assistant,
            [
                new ThinkingBlock("t", "sig"),
                new ToolUseBlock("t1", "echo", document.RootElement.Clone()),
            ]),
            new Message(MessageRole.User, [new ToolResultBlock("t1", "r", false)]),
        ]);

        var restored = Conversation.FromJson(conversation.ToJson());

        await Assert.That(restored.Id).IsEqualTo(conversation.Id);
        await Assert.That(restored.ToJson()).IsEqualTo(conversation.ToJson());
    }

    [Test]
    public async Task Conversation_json_rejects_null_literal()
    {
        await Assert.That(() => Conversation.FromJson("null")).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task In_memory_store_round_trips_and_deletes()
    {
        var store = new InMemoryConversationStore();
        var conversation = Conversation.Start().Append(Message.User("x"));

        await Assert.That(async () => { await store.SaveAsync(null!); }).Throws<ArgumentNullException>();
        await store.SaveAsync(conversation);
        await Assert.That((await store.LoadAsync(conversation.Id))!.Id).IsEqualTo(conversation.Id);
        await store.DeleteAsync(conversation.Id);
        await Assert.That(await store.LoadAsync(conversation.Id)).IsNull();
    }

    [Test]
    public async Task Session_validates_arguments()
    {
        var (agent, _) = Agent();
        var store = new InMemoryConversationStore();
        await Assert.That(() => new ConversationSession(null!, store, ConversationId.New())).Throws<ArgumentNullException>();
        await Assert.That(() => new ConversationSession(agent, null!, ConversationId.New())).Throws<ArgumentNullException>();
    }
}
