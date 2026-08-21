using System.Diagnostics;
using System.Diagnostics.Metrics;
using Emissary.Tests.Agents;
using Emissary.Tests.Tools;
using Emissary.Transport;

namespace Emissary.Tests;

public sealed class BudgetTests
{
    private static (ClaudeAgent Agent, FakeTransport Transport) Create(Action<AgentOptions> configure)
    {
        var options = new AgentOptions();
        configure(options);
        var transport = new FakeTransport();
        return (new ClaudeAgent(options, transport), transport);
    }

    [Test]
    public async Task Budget_stops_the_run_when_exceeded()
    {
        var (agent, transport) = Create(options =>
        {
            options.TokenBudget = 25;
            options.Tools.Add(SampleTools.EchoTool);
        });
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "echo", """{"text":"a"}""")));
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t2", "echo", """{"text":"b"}""")));

        var result = await agent.RunAsync("go");

        await Assert.That(result.StopReason).IsEqualTo(AgentStopReason.BudgetExceeded);
        await Assert.That(transport.Requests.Count).IsEqualTo(2);
        await Assert.That(result.Usage.InputTokens + result.Usage.OutputTokens).IsEqualTo(30);
    }

    [Test]
    public async Task Budget_with_headroom_does_not_interfere()
    {
        var (agent, transport) = Create(options => options.TokenBudget = 1000);
        transport.EnqueueTurn(FakeTransport.TextTurn("fine"));

        var result = await agent.RunAsync("go");

        await Assert.That(result.StopReason).IsEqualTo(AgentStopReason.Completed);
    }

    [Test]
    public async Task Budget_is_validated()
    {
        await Assert.That(() => new ClaudeAgent(new AgentOptions { TokenBudget = 0 }, new FakeTransport()))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Cache_usage_flows_into_the_result()
    {
        var (agent, transport) = Create(_ => { });
        transport.EnqueueTurn(new StreamCompleted(new ModelResponse(
            [new TextBlock("hi")], "end_turn", 5, 3, CacheCreationInputTokens: 100, CacheReadInputTokens: 400)));

        var result = await agent.RunAsync("go");

        await Assert.That(result.Usage).IsEqualTo(new AgentUsage(5, 3, 100, 400));
    }
}

[NotInParallel]
public sealed class DiagnosticsTests
{
    private const string Model = "otel-test-model";

    [Test]
    public async Task Run_emits_gen_ai_spans_and_metrics()
    {
        var stopped = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Emissary",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var measurements = new List<(string Instrument, long Value)>();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Emissary")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Value as string is Model or "echo")
                {
                    lock (measurements)
                    {
                        measurements.Add((instrument.Name, value));
                    }

                    break;
                }
            }
        });
        meterListener.Start();

        var options = new AgentOptions { Model = Model, Tools = { SampleTools.EchoTool } };
        var transport = new FakeTransport();
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "echo", """{"text":"x"}""")));
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t2", "missing-tool", "{}")));
        transport.EnqueueTurn(FakeTransport.TextTurn("done"));
        var agent = new ClaudeAgent(options, transport);

        await agent.RunAsync("go");

        var run = stopped.Single(a => a.OperationName.StartsWith("invoke_agent", StringComparison.Ordinal));
        await Assert.That(run.GetTagItem("gen_ai.request.model")).IsEqualTo(Model);
        await Assert.That(run.GetTagItem("emissary.stop_reason")).IsEqualTo("Completed");

        var chats = stopped.Where(a => a.OperationName.StartsWith("chat", StringComparison.Ordinal)).ToList();
        await Assert.That(chats.Count).IsEqualTo(3);
        await Assert.That(chats[0].GetTagItem("gen_ai.usage.input_tokens")).IsEqualTo(10L);

        var tools = stopped.Where(a => a.OperationName.StartsWith("execute_tool", StringComparison.Ordinal)).ToList();
        await Assert.That(tools.Count).IsEqualTo(2);
        await Assert.That(tools.Single(t => Equals(t.GetTagItem("gen_ai.tool.name"), "missing-tool")).Status)
            .IsEqualTo(ActivityStatusCode.Error);

        long inputTotal;
        lock (measurements)
        {
            inputTotal = measurements.Where(m => m.Instrument == "emissary.usage.input_tokens").Sum(m => m.Value);
        }

        await Assert.That(inputTotal).IsEqualTo(30L);
    }
}
