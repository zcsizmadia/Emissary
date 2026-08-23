using System.Text.Json;
using Emissary.Tests.Agents;
using Emissary.Tests.Tools;

namespace Emissary.Tests;

/// <summary>
/// A tool that throws or hangs is an operational event, not a reason to lose the conversation —
/// and what the model is told about it must not leak the exception's internals by default.
/// </summary>
public sealed class ToolFailureTests
{
    private const string EmptySchema = """{"type":"object","properties":{}}""";

    private static ToolDefinition Throwing(Exception exception) =>
        new("break", "Always fails.", EmptySchema, (_, _) => throw exception);

    private static ToolDefinition Hanging(TaskCompletionSource? started = null) =>
        new("hang", "Never returns.", EmptySchema, async (_, token) =>
        {
            started?.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return "unreachable";
        });

    private static (ClaudeAgent Agent, FakeTransport Transport) Create(
        ToolDefinition tool,
        Action<AgentOptions>? configure = null)
    {
        var options = new AgentOptions();
        options.Tools.Add(tool);
        configure?.Invoke(options);
        var transport = new FakeTransport();
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", tool.Name, "{}")));
        transport.EnqueueTurn(FakeTransport.TextTurn("I could not do that."));
        return (new ClaudeAgent(options, transport), transport);
    }

    private static ToolResultBlock ResultSeenByModel(FakeTransport transport) =>
        (ToolResultBlock)transport.Requests[1].Messages[^1].Content.Single();

    [Test]
    public async Task A_throwing_tool_is_reported_to_the_model_and_the_run_continues()
    {
        var (agent, transport) = Create(Throwing(new InvalidOperationException("connect to db=prod;pwd=hunter2")));

        var result = await agent.RunAsync("go");

        var seen = ResultSeenByModel(transport);
        await Assert.That(seen.IsError).IsTrue();
        await Assert.That(seen.Content).IsEqualTo("Tool 'break' failed with InvalidOperationException.");
        await Assert.That(result.StopReason).IsEqualTo(AgentStopReason.Completed);
        await Assert.That(result.FinalText).IsEqualTo("I could not do that.");
    }

    [Test]
    public async Task The_exception_message_is_withheld_from_the_model_but_given_to_the_caller()
    {
        var thrown = new InvalidOperationException("connect to db=prod;pwd=hunter2");
        var (agent, transport) = Create(Throwing(thrown));

        var result = await agent.RunAsync("go");

        // Nothing from the message reaches the API-bound content.
        await Assert.That(ResultSeenByModel(transport).Content).DoesNotContain("hunter2");

        // The caller gets the exception itself.
        var failure = result.ToolFailures.Single();
        await Assert.That(failure.ToolName).IsEqualTo("break");
        await Assert.That(failure.ToolUseId).IsEqualTo("t1");
        await Assert.That(failure.TimedOut).IsFalse();
        await Assert.That(failure.Exception).IsSameReferenceAs(thrown);
    }

    [Test]
    public async Task The_message_can_be_opted_into()
    {
        var (agent, transport) = Create(
            Throwing(new HttpRequestException("order service returned 503")),
            o => o.ToolFailures.IncludeExceptionMessage = true);

        await agent.RunAsync("go");

        await Assert.That(ResultSeenByModel(transport).Content)
            .IsEqualTo("Tool 'break' failed with HttpRequestException: order service returned 503");
    }

    [Test]
    public async Task A_failure_is_streamed_as_an_event()
    {
        var (agent, _) = Create(Throwing(new InvalidOperationException("boom")));

        var events = new List<AgentEvent>();
        await foreach (var e in agent.StreamAsync("go"))
        {
            events.Add(e);
        }

        var failed = events.OfType<AgentToolFailedEvent>().Single();
        await Assert.That(failed.Failure.ToolName).IsEqualTo("break");
        await Assert.That(failed.Failure.Exception.Message).IsEqualTo("boom");

        // The failure event precedes the tool result the model sees.
        await Assert.That(events.IndexOf(failed))
            .IsLessThan(events.FindIndex(e => e is AgentToolResultEvent));
    }

    [Test]
    public async Task Propagate_mode_lets_the_exception_out_of_the_run()
    {
        var (agent, _) = Create(
            Throwing(new InvalidOperationException("boom")),
            o => o.ToolFailures.Mode = ToolFailureMode.Propagate);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () => await agent.RunAsync("go"));

        await Assert.That(thrown!.Message).IsEqualTo("boom");
    }

    [Test]
    public async Task A_tool_that_never_returns_is_cancelled_and_reported()
    {
        var (agent, transport) = Create(
            Hanging(),
            o => o.ToolFailures.Timeout = TimeSpan.FromMilliseconds(50));

        var result = await agent.RunAsync("go");

        await Assert.That(ResultSeenByModel(transport).Content).IsEqualTo(
            "Tool 'hang' was cancelled after 0.05s without finishing. "
            + "Try a narrower request, or continue without it.");
        var failure = result.ToolFailures.Single();
        await Assert.That(failure.TimedOut).IsTrue();
        await Assert.That(failure.Exception).IsTypeOf<TaskCanceledException>();
    }

    [Test]
    public async Task A_timeout_is_per_call_so_a_fast_tool_is_unaffected()
    {
        var options = new AgentOptions();
        options.Tools.Add(SampleTools.EchoTool);
        options.ToolFailures.Timeout = TimeSpan.FromSeconds(30);
        var transport = new FakeTransport();
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "echo", """{"text":"hi"}""")));
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t2", "echo", """{"text":"again"}""")));
        transport.EnqueueTurn(FakeTransport.TextTurn("done"));

        var result = await new ClaudeAgent(options, transport).RunAsync("go");

        await Assert.That(result.ToolFailures).IsEmpty();
        await Assert.That(((ToolResultBlock)transport.Requests[2].Messages[^1].Content.Single()).Content)
            .IsEqualTo("again");
    }

    [Test]
    public async Task Cancelling_the_run_is_not_treated_as_a_tool_failure()
    {
        var started = new TaskCompletionSource();
        var (agent, _) = Create(
            Hanging(started),
            o => o.ToolFailures.Timeout = TimeSpan.FromMinutes(5));

        using var cancellation = new CancellationTokenSource();
        var run = agent.RunAsync("go", cancellation.Token);
        await started.Task;
        await cancellation.CancelAsync();

        // Cancellation surfaces as cancellation, not as a tool that failed.
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await run);
    }

    [Test]
    public async Task A_failing_tool_does_not_satisfy_a_contract_prerequisite()
    {
        var options = new AgentOptions();
        options.Tools.Add(Throwing(new InvalidOperationException("boom")));
        options.Tools.Add(SampleTools.EchoTool);
        options.Rules.Require("echo", prerequisite: "break");
        var transport = new FakeTransport();
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "break", "{}")));
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t2", "echo", """{"text":"hi"}""")));
        transport.EnqueueTurn(FakeTransport.TextTurn("done"));

        await new ClaudeAgent(options, transport).RunAsync("go");

        var blocked = (ToolResultBlock)transport.Requests[2].Messages[^1].Content.Single();
        await Assert.That(blocked.IsError).IsTrue();
        await Assert.That(blocked.Content).Contains("requires a prior successful call to 'break'");
    }

    [Test]
    public async Task Failures_from_both_sides_of_a_handoff_reach_the_result()
    {
        var specialistOptions = new AgentOptions();
        specialistOptions.Tools.Add(Throwing(new TimeoutException("specialist tool down")));
        var specialistTransport = new FakeTransport();
        specialistTransport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("s1", "break", "{}")));
        specialistTransport.EnqueueTurn(FakeTransport.TextTurn("handled without it"));
        var specialist = new ClaudeAgent(specialistOptions, specialistTransport);

        var triageOptions = new AgentOptions();
        triageOptions.Tools.Add(Throwing(new InvalidOperationException("triage tool down")));
        triageOptions.Handoffs.Add(new HandoffTarget("billing", specialist, "Billing."));
        var triageTransport = new FakeTransport();
        triageTransport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "break", "{}")));
        triageTransport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t2", "handoff_to_billing", "{}")));

        var result = await new ClaudeAgent(triageOptions, triageTransport).RunAsync("go");

        await Assert.That(result.ToolFailures.Select(f => f.Exception.GetType().Name))
            .IsEquivalentTo(["InvalidOperationException", "TimeoutException"]);
    }

    [Test]
    public async Task An_approved_call_that_fails_after_resuming_is_reported()
    {
        var options = new AgentOptions();
        options.Tools.Add(new ToolDefinition(
            "pay", "Sends a payment.", EmptySchema,
            (_, _) => throw new HttpRequestException("gateway down"),
            privileged: true));
        options.ApprovalRequired = tool => tool.Name == "pay";
        var transport = new FakeTransport();
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "pay", "{}")));
        transport.EnqueueTurn(FakeTransport.TextTurn("the payment did not go through"));
        var agent = new ClaudeAgent(options, transport);

        var suspended = await agent.RunAsync("pay the invoice");
        await Assert.That(suspended.StopReason).IsEqualTo(AgentStopReason.AwaitingApproval);

        var events = new List<AgentEvent>();
        await foreach (var e in agent.ResumeStreamAsync(suspended.Suspension!, approve: true))
        {
            events.Add(e);
        }

        var failed = events.OfType<AgentToolFailedEvent>().Single();
        await Assert.That(failed.Failure.ToolName).IsEqualTo("pay");
        var completed = events.OfType<AgentCompletedEvent>().Single().Result;
        await Assert.That(completed.ToolFailures.Single().Exception).IsTypeOf<HttpRequestException>();
        await Assert.That(completed.FinalText).IsEqualTo("the payment did not go through");
    }

    [Test]
    public async Task Argument_binding_errors_are_not_counted_as_tool_failures()
    {
        // A ToolArgumentException is the model's mistake, already reported as an error result;
        // it is not an exception escaping the handler.
        var options = new AgentOptions();
        options.Tools.Add(new ToolDefinition(
            "strict", "Needs an argument.", EmptySchema,
            (JsonElement input, CancellationToken token) => input.TryGetProperty("id", out _)
                ? new ValueTask<string>("ok")
                : throw new ToolArgumentException("Tool 'strict' is missing required argument 'id'.")));
        var transport = new FakeTransport();
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "strict", "{}")));
        transport.EnqueueTurn(FakeTransport.TextTurn("sorry"));

        var result = await new ClaudeAgent(options, transport).RunAsync("go");

        await Assert.That(result.ToolFailures).IsEmpty();
        await Assert.That(ResultSeenByModel(transport).Content)
            .IsEqualTo("Tool 'strict' is missing required argument 'id'.");
    }
}
