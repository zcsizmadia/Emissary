using Emissary.Extensions.AI;
using Emissary.Tests.Agents;
using Emissary.Tests.Tools;
using Microsoft.Extensions.AI;

namespace Emissary.Tests;

public sealed class ChatClientAdapterTests
{
    private static (EmissaryChatClient Client, FakeTransport Transport) Create(Action<AgentOptions>? configure = null)
    {
        var options = new AgentOptions();
        configure?.Invoke(options);
        var transport = new FakeTransport();
        return (new EmissaryChatClient(new ClaudeAgent(options, transport)), transport);
    }

    [Test]
    public async Task GetResponseAsync_returns_the_agents_answer_with_usage()
    {
        var (client, transport) = Create();
        transport.EnqueueTurn(FakeTransport.TextTurn("Oslo", input: 11, output: 3));

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Capital of Norway?")]);

        await Assert.That(response.Text).IsEqualTo("Oslo");
        await Assert.That(response.FinishReason).IsEqualTo(ChatFinishReason.Stop);
        await Assert.That(response.Usage!.InputTokenCount).IsEqualTo(11);
        await Assert.That(response.Usage!.OutputTokenCount).IsEqualTo(3);
        await Assert.That(response.Usage!.TotalTokenCount).IsEqualTo(14);
        await Assert.That(response.ModelId).IsEqualTo(EmissaryDefaults.Model);
    }

    [Test]
    public async Task Prior_turns_are_forwarded_to_the_agent()
    {
        var (client, transport) = Create();
        transport.EnqueueTurn(FakeTransport.TextTurn("sure"));

        await client.GetResponseAsync([
            new ChatMessage(ChatRole.User, "first"),
            new ChatMessage(ChatRole.Assistant, "reply"),
            new ChatMessage(ChatRole.User, "second"),
        ]);

        var sent = transport.Requests.Single().Messages;
        await Assert.That(sent.Count).IsEqualTo(3);
        await Assert.That(sent[0].Text).IsEqualTo("first");
        await Assert.That(sent[1].Role).IsEqualTo(MessageRole.Assistant);
        await Assert.That(sent[2].Text).IsEqualTo("second");
    }

    [Test]
    public async Task System_and_tool_messages_are_ignored_so_agent_configuration_wins()
    {
        var (client, transport) = Create(o => o.SystemPrompt = "configured prompt");
        transport.EnqueueTurn(FakeTransport.TextTurn("ok"));

        await client.GetResponseAsync([
            new ChatMessage(ChatRole.System, "caller tries to override"),
            new ChatMessage(ChatRole.Tool, "tool chatter"),
            new ChatMessage(ChatRole.User, "hello"),
        ]);

        var request = transport.Requests.Single();
        await Assert.That(request.Messages.Single().Text).IsEqualTo("hello");
        await Assert.That(request.System).IsEqualTo("configured prompt");
    }

    [Test]
    public async Task Streaming_yields_text_updates_then_a_finish_reason()
    {
        var (client, transport) = Create();
        transport.EnqueueTurn(
            new Emissary.Transport.StreamTextDelta("Os"),
            new Emissary.Transport.StreamTextDelta("lo"),
            FakeTransport.TextTurn("Oslo"));

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "capital?")]))
        {
            updates.Add(update);
        }

        await Assert.That(string.Concat(updates.Select(u => u.Text))).IsEqualTo("Oslo");
        await Assert.That(updates[^1].FinishReason).IsEqualTo(ChatFinishReason.Stop);
        await Assert.That(updates.All(u => u.Role == ChatRole.Assistant)).IsTrue();
    }

    [Test]
    public async Task Abandoning_the_stream_early_disposes_cleanly()
    {
        var (client, transport) = Create();
        transport.EnqueueTurn(
            new Emissary.Transport.StreamTextDelta("first"),
            new Emissary.Transport.StreamTextDelta("second"),
            FakeTransport.TextTurn("firstsecond"));

        ChatResponseUpdate? first = null;
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "go")]))
        {
            first = update;
            break;
        }

        await Assert.That(first!.Text).IsEqualTo("first");
    }

    [Test]
    [Arguments(AgentStopReason.Completed, "stop")]
    [Arguments(AgentStopReason.MaxTokens, "length")]
    [Arguments(AgentStopReason.Refusal, "content_filter")]
    [Arguments(AgentStopReason.TurnLimit, "tool_calls")]
    [Arguments(AgentStopReason.BudgetExceeded, "tool_calls")]
    [Arguments(AgentStopReason.AwaitingApproval, "tool_calls")]
    public async Task Stop_reasons_map_to_chat_finish_reasons(AgentStopReason stopReason, string expected)
    {
        await Assert.That(EmissaryChatClient.ToFinishReason(stopReason).Value).IsEqualTo(expected);
    }

    [Test]
    public async Task GetService_exposes_metadata_the_agent_and_itself()
    {
        var (client, _) = Create();

        await Assert.That(client.GetService(typeof(ChatClientMetadata))).IsTypeOf<ChatClientMetadata>();
        await Assert.That(client.GetService(typeof(ClaudeAgent))).IsTypeOf<ClaudeAgent>();
        await Assert.That(client.GetService(typeof(IChatClient))).IsSameReferenceAs(client);
        await Assert.That(client.GetService(typeof(string))).IsNull();
        await Assert.That(client.GetService(typeof(IChatClient), serviceKey: "keyed")).IsNull();
        await Assert.That(() => client.GetService(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Metadata_reports_a_custom_model_id()
    {
        var client = new EmissaryChatClient(
            new ClaudeAgent(new AgentOptions(), new FakeTransport()), modelId: "custom-model");

        var metadata = (ChatClientMetadata)client.GetService(typeof(ChatClientMetadata))!;
        await Assert.That(metadata.DefaultModelId).IsEqualTo("custom-model");
        await Assert.That(metadata.ProviderName).IsEqualTo("Emissary");
    }

    [Test]
    public async Task Dispose_is_safe_and_arguments_are_validated()
    {
        var (client, _) = Create();
        client.Dispose();

        await Assert.That(() => new EmissaryChatClient(null!)).Throws<ArgumentNullException>();
        await Assert.That(() => EmissaryChatClient.ToConversation(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Tools_and_contracts_still_apply_through_the_adapter()
    {
        var (client, transport) = Create(o =>
        {
            o.Tools.Add(SampleTools.EchoTool);
            o.Tools.Add(SampleTools.AddTool);
            o.Rules.Require("add", "echo");
        });
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "add", """{"left":1}""")));
        transport.EnqueueTurn(FakeTransport.TextTurn("blocked as expected"));

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "add please")]);

        // The contract fired inside the agent, so the caller sees the corrected outcome.
        var toolResult = (ToolResultBlock)transport.Requests[1].Messages[^1].Content.Single();
        await Assert.That(toolResult.IsError).IsTrue();
        await Assert.That(toolResult.Content).Contains("requires a prior successful call to 'echo'");
        await Assert.That(response.Text).IsEqualTo("blocked as expected");
    }
}
