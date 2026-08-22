using System.Text.Json;
using System.Text.Json.Serialization;

namespace Emissary.Tests;

internal sealed record TypedAnswer(string Text, int Score);

[JsonSerializable(typeof(TypedAnswer))]
[JsonSerializable(typeof(Tools.WeatherReport))]
internal sealed partial class TestJsonContext : JsonSerializerContext;

public sealed class ConversationModelTests
{
    private static AgentResult ResultWithFinalText(string text) => new()
    {
        Conversation = Conversation.Start()
            .Append(Message.User("q"))
            .Append(new Message(MessageRole.Assistant, [new TextBlock(text)])),
        StopReason = AgentStopReason.Completed,
        Usage = AgentUsage.Zero,
    };

    [Test]
    public async Task FinalAs_deserializes_structured_output()
    {
        var result = ResultWithFinalText("""{"Text":"ok","Score":9}""");

        var answer = result.FinalAs(TestJsonContext.Default.TypedAnswer);

        await Assert.That(answer).IsEqualTo(new TypedAnswer("ok", 9));
    }

    [Test]
    public async Task FinalAs_throws_when_output_is_null()
    {
        var result = ResultWithFinalText("null");

        await Assert.That(() => result.FinalAs(TestJsonContext.Default.TypedAnswer))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Conversation_starts_empty_with_fresh_id()
    {
        var conversation = Conversation.Start();

        await Assert.That(conversation.Messages.Count).IsEqualTo(0);
        await Assert.That(conversation.Id).IsNotEqualTo(Conversation.Start().Id);
    }

    [Test]
    public async Task Append_is_immutable_and_keeps_the_id()
    {
        var start = Conversation.Start();

        var appended = start.Append(Message.User("hello"));

        await Assert.That(start.Messages.Count).IsEqualTo(0);
        await Assert.That(appended.Messages.Count).IsEqualTo(1);
        await Assert.That(appended.Id).IsEqualTo(start.Id);
    }

    [Test]
    public async Task Append_null_throws()
    {
        await Assert.That(() => Conversation.Start().Append(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Message_text_concatenates_only_text_blocks()
    {
        using var document = JsonDocument.Parse("{}");
        var message = new Message(MessageRole.Assistant,
        [
            new ThinkingBlock("pondering", null),
            new TextBlock("Hello, "),
            new ToolUseBlock("t1", "echo", document.RootElement),
            new TextBlock("world"),
        ]);

        await Assert.That(message.Text).IsEqualTo("Hello, world");
    }

    [Test]
    public async Task User_helper_builds_single_text_block()
    {
        var message = Message.User("hi");

        await Assert.That(message.Role).IsEqualTo(MessageRole.User);
        await Assert.That(message.Content.Single()).IsEqualTo(new TextBlock("hi"));
    }

    [Test]
    public async Task Usage_zero_and_add()
    {
        await Assert.That(AgentUsage.Zero).IsEqualTo(new AgentUsage(0, 0));
        await Assert.That(AgentUsage.Zero.Add(3, 4).Add(1, 1)).IsEqualTo(new AgentUsage(4, 5));
    }

    [Test]
    public async Task FinalText_is_empty_without_an_assistant_message()
    {
        var result = new AgentResult
        {
            Conversation = Conversation.Start().Append(Message.User("hi")),
            StopReason = AgentStopReason.Completed,
            Usage = AgentUsage.Zero,
        };

        await Assert.That(result.FinalText).IsEqualTo("");
    }
}
