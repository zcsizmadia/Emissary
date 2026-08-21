using Emissary.Tests.Agents;
using Emissary.Tests.Tools;
using Emissary.Transport;

namespace Emissary.Tests;

public sealed class ClaudeAgentTests
{
    private static (ClaudeAgent Agent, FakeTransport Transport) Create(Action<AgentOptions>? configure = null)
    {
        var options = new AgentOptions();
        configure?.Invoke(options);
        var transport = new FakeTransport();
        return (new ClaudeAgent(options, transport), transport);
    }

    [Test]
    public async Task Text_run_completes_with_final_text_and_usage()
    {
        var (agent, transport) = Create();
        transport.EnqueueTurn(
            new StreamTextDelta("Hel"),
            new StreamTextDelta("lo"),
            FakeTransport.TextTurn("Hello", input: 12, output: 7));

        var result = await agent.RunAsync("hi");

        await Assert.That(result.StopReason).IsEqualTo(AgentStopReason.Completed);
        await Assert.That(result.FinalText).IsEqualTo("Hello");
        await Assert.That(result.Usage).IsEqualTo(new AgentUsage(12, 7));
        await Assert.That(result.Conversation.Messages.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Streaming_yields_text_thinking_turn_and_completed_events()
    {
        var (agent, transport) = Create();
        transport.EnqueueTurn(
            new StreamThinkingDelta("hmm"),
            new StreamTextDelta("Hi"),
            FakeTransport.TextTurn("Hi"));

        var events = new List<AgentEvent>();
        await foreach (var agentEvent in agent.StreamAsync("hello"))
        {
            events.Add(agentEvent);
        }

        await Assert.That(events[0]).IsEqualTo(new AgentThinkingEvent("hmm"));
        await Assert.That(events[1]).IsEqualTo(new AgentTextEvent("Hi"));
        await Assert.That(events[2]).IsTypeOf<AgentTurnEvent>();
        await Assert.That(events[3]).IsTypeOf<AgentCompletedEvent>();
    }

    [Test]
    public async Task Run_builds_request_from_options_and_input()
    {
        var (agent, transport) = Create(options =>
        {
            options.Model = "claude-opus-5";
            options.SystemPrompt = "Be terse.";
            options.MaxTokens = 999;
            options.Effort = EffortLevel.High;
            options.Thinking = ThinkingMode.Disabled;
            options.Tools.Add(SampleTools.EchoTool);
        });
        transport.EnqueueTurn(FakeTransport.TextTurn("ok"));

        await agent.RunAsync("question");

        var request = transport.Requests.Single();
        await Assert.That(request.Model).IsEqualTo("claude-opus-5");
        await Assert.That(request.System).IsEqualTo("Be terse.");
        await Assert.That(request.MaxTokens).IsEqualTo(999);
        await Assert.That(request.Effort).IsEqualTo(EffortLevel.High);
        await Assert.That(request.Thinking).IsEqualTo(ThinkingMode.Disabled);
        await Assert.That(request.Tools.Single().Name).IsEqualTo("echo");
        await Assert.That(request.Messages.Single().Text).IsEqualTo("question");
    }

    [Test]
    public async Task Tool_loop_executes_tool_and_feeds_result_back()
    {
        var (agent, transport) = Create(options => options.Tools.Add(SampleTools.EchoTool));
        transport.EnqueueTurn(
            new StreamToolUseStart("t1", "echo"),
            FakeTransport.ToolTurn(FakeTransport.Use("t1", "echo", """{"text":"ping"}""")));
        transport.EnqueueTurn(FakeTransport.TextTurn("done"));

        var events = new List<AgentEvent>();
        await foreach (var agentEvent in agent.StreamAsync("go"))
        {
            events.Add(agentEvent);
        }

        await Assert.That(events.OfType<AgentToolCallEvent>().Single()).IsEqualTo(new AgentToolCallEvent("t1", "echo"));
        await Assert.That(events.OfType<AgentToolResultEvent>().Single())
            .IsEqualTo(new AgentToolResultEvent("t1", "echo", "ping", false));

        var followUp = transport.Requests[1];
        await Assert.That(followUp.Messages.Count).IsEqualTo(3);
        var toolResult = (ToolResultBlock)followUp.Messages[2].Content.Single();
        await Assert.That(toolResult).IsEqualTo(new ToolResultBlock("t1", "ping", false));

        var result = events.OfType<AgentCompletedEvent>().Single().Result;
        await Assert.That(result.FinalText).IsEqualTo("done");
        await Assert.That(result.Conversation.Messages.Count).IsEqualTo(4);
    }

    [Test]
    public async Task Parallel_tool_calls_run_and_results_keep_order()
    {
        var (agent, transport) = Create(options =>
        {
            options.Tools.Add(SampleTools.EchoTool);
            options.Tools.Add(SampleTools.AddTool);
        });
        transport.EnqueueTurn(FakeTransport.ToolTurn(
            FakeTransport.Use("t1", "echo", """{"text":"a"}"""),
            FakeTransport.Use("t2", "add", """{"left":2,"right":3}""")));
        transport.EnqueueTurn(FakeTransport.TextTurn("done"));

        await agent.RunAsync("go");

        var resultsMessage = transport.Requests[1].Messages[2];
        var results = resultsMessage.Content.Cast<ToolResultBlock>().ToArray();
        await Assert.That(results[0]).IsEqualTo(new ToolResultBlock("t1", "a", false));
        await Assert.That(results[1]).IsEqualTo(new ToolResultBlock("t2", "5", false));
    }

    [Test]
    public async Task Unknown_tool_returns_error_result()
    {
        var (agent, transport) = Create();
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "nope", "{}")));
        transport.EnqueueTurn(FakeTransport.TextTurn("done"));

        await agent.RunAsync("go");

        var toolResult = (ToolResultBlock)transport.Requests[1].Messages[2].Content.Single();
        await Assert.That(toolResult.IsError).IsTrue();
        await Assert.That(toolResult.Content).IsEqualTo("Unknown tool 'nope'.");
    }

    [Test]
    public async Task Invalid_tool_arguments_become_error_result()
    {
        var (agent, transport) = Create(options => options.Tools.Add(SampleTools.EchoTool));
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "echo", "{}")));
        transport.EnqueueTurn(FakeTransport.TextTurn("done"));

        await agent.RunAsync("go");

        var toolResult = (ToolResultBlock)transport.Requests[1].Messages[2].Content.Single();
        await Assert.That(toolResult.IsError).IsTrue();
        await Assert.That(toolResult.Content).Contains("missing required argument 'text'");
    }

    [Test]
    [Arguments("max_tokens", AgentStopReason.MaxTokens)]
    [Arguments("refusal", AgentStopReason.Refusal)]
    [Arguments("stop_sequence", AgentStopReason.Completed)]
    public async Task Stop_reasons_map_to_agent_stop_reasons(string stopReason, AgentStopReason expected)
    {
        var (agent, transport) = Create();
        transport.EnqueueTurn(FakeTransport.TextTurn("partial", stopReason));

        var result = await agent.RunAsync("go");

        await Assert.That(result.StopReason).IsEqualTo(expected);
    }

    [Test]
    public async Task Turn_limit_stops_a_tool_loop_that_never_converges()
    {
        var (agent, transport) = Create(options =>
        {
            options.MaxTurns = 2;
            options.Tools.Add(SampleTools.EchoTool);
        });
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "echo", """{"text":"x"}""")));
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t2", "echo", """{"text":"y"}""")));

        var result = await agent.RunAsync("go");

        await Assert.That(result.StopReason).IsEqualTo(AgentStopReason.TurnLimit);
        await Assert.That(result.Usage).IsEqualTo(new AgentUsage(20, 10));
        await Assert.That(transport.Requests.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Transport_stream_without_completion_is_a_contract_violation()
    {
        var (agent, transport) = Create();
        transport.EnqueueTurn(new StreamTextDelta("oops"));

        await Assert.That(async () => { await agent.RunAsync("go"); }).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Cancellation_propagates()
    {
        var (agent, transport) = Create();
        transport.EnqueueTurn(FakeTransport.TextTurn("late"));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.That(async () => { await agent.RunAsync("go", cancellation.Token); })
            .Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Null_conversation_throws_on_enumeration()
    {
        var (agent, _) = Create();

        await Assert.That(async () =>
            {
                await foreach (var _ in agent.StreamAsync((Conversation)null!))
                {
                }
            })
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Abandoning_the_stream_early_disposes_cleanly()
    {
        var (agent, transport) = Create();
        transport.EnqueueTurn(
            new StreamTextDelta("first"),
            new StreamTextDelta("second"),
            FakeTransport.TextTurn("full"));

        AgentEvent? first = null;
        await foreach (var agentEvent in agent.StreamAsync("go"))
        {
            first = agentEvent;
            break;
        }

        await Assert.That(first).IsEqualTo(new AgentTextEvent("first"));
    }

    [Test]
    public async Task Abandoning_the_stream_between_turns_disposes_cleanly()
    {
        var (agent, transport) = Create(options => options.Tools.Add(SampleTools.EchoTool));
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "echo", """{"text":"x"}""")));
        transport.EnqueueTurn(FakeTransport.TextTurn("never read"));

        await foreach (var agentEvent in agent.StreamAsync("go"))
        {
            if (agentEvent is AgentToolResultEvent)
            {
                break;
            }
        }

        await Assert.That(transport.Requests.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Abandoning_the_stream_at_the_turn_limit_event_disposes_cleanly()
    {
        var (agent, transport) = Create(options =>
        {
            options.MaxTurns = 1;
            options.Tools.Add(SampleTools.EchoTool);
        });
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "echo", """{"text":"x"}""")));

        AgentResult? result = null;
        await foreach (var agentEvent in agent.StreamAsync("go"))
        {
            if (agentEvent is AgentCompletedEvent completed)
            {
                result = completed.Result;
                break;
            }
        }

        await Assert.That(result!.StopReason).IsEqualTo(AgentStopReason.TurnLimit);
    }

    [Test]
    public async Task Options_are_validated()
    {
        await Assert.That(() => new ClaudeAgent(null!)).Throws<ArgumentNullException>();
        await Assert.That(() => new ClaudeAgent(new AgentOptions { MaxTurns = 0 }, new FakeTransport()))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new ClaudeAgent(new AgentOptions { MaxTokens = 0 }, new FakeTransport()))
            .Throws<ArgumentOutOfRangeException>();

        var duplicates = new AgentOptions();
        duplicates.Tools.Add(SampleTools.EchoTool);
        duplicates.Tools.Add(SampleTools.EchoTool);
        await Assert.That(() => new ClaudeAgent(duplicates, new FakeTransport())).Throws<ArgumentException>();
    }
}
