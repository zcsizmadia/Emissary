using Emissary.Testing;
using Emissary.Tests.Agents;
using Emissary.Tests.Tools;

namespace Emissary.Tests;

public sealed class CompositionTests
{
    private static (ClaudeAgent Agent, FakeTransport Transport) CreateSubAgent(params ToolDefinition[] tools)
    {
        var options = new AgentOptions();
        foreach (var tool in tools)
        {
            options.Tools.Add(tool);
        }

        var transport = new FakeTransport();
        return (new ClaudeAgent(options, transport), transport);
    }

    [Test]
    public async Task Sub_agent_runs_as_a_tool_of_the_parent()
    {
        var (subAgent, subTransport) = CreateSubAgent();
        subTransport.EnqueueTurn(FakeTransport.TextTurn("the sub-agent's answer"));

        var parentOptions = new AgentOptions
        {
            Tools = { subAgent.AsTool("researcher", "Delegates research questions.") },
        };
        var parentTransport = new FakeTransport();
        parentTransport.EnqueueTurn(FakeTransport.ToolTurn(
            FakeTransport.Use("t1", "researcher", """{"message":"look this up"}""")));
        parentTransport.EnqueueTurn(FakeTransport.TextTurn("done"));
        var parent = new ClaudeAgent(parentOptions, parentTransport);

        await parent.RunAsync("go");

        var delegated = (ToolResultBlock)parentTransport.Requests[1].Messages[^1].Content.Single();
        await Assert.That(delegated.IsError).IsFalse();
        await Assert.That(delegated.Content).IsEqualTo("the sub-agent's answer");
        await Assert.That(subTransport.Requests.Single().Messages.Single().Text).IsEqualTo("look this up");
    }

    [Test]
    public async Task Missing_message_argument_is_a_tool_error()
    {
        var (subAgent, _) = CreateSubAgent();
        var parentOptions = new AgentOptions { Tools = { subAgent.AsTool("researcher", "d") } };
        var parentTransport = new FakeTransport();
        parentTransport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "researcher", "{}")));
        parentTransport.EnqueueTurn(FakeTransport.TextTurn("done"));
        var parent = new ClaudeAgent(parentOptions, parentTransport);

        await parent.RunAsync("go");

        var result = (ToolResultBlock)parentTransport.Requests[1].Messages[^1].Content.Single();
        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Content).Contains("missing required argument 'message'");
    }

    [Test]
    public async Task Abnormal_sub_agent_stops_surface_as_tool_errors()
    {
        var (subAgent, subTransport) = CreateSubAgent();
        subTransport.EnqueueTurn(FakeTransport.TextTurn("partial", "max_tokens"));

        var parentOptions = new AgentOptions { Tools = { subAgent.AsTool("researcher", "d") } };
        var parentTransport = new FakeTransport();
        parentTransport.EnqueueTurn(FakeTransport.ToolTurn(
            FakeTransport.Use("t1", "researcher", """{"message":"q"}""")));
        parentTransport.EnqueueTurn(FakeTransport.TextTurn("done"));
        var parent = new ClaudeAgent(parentOptions, parentTransport);

        await parent.RunAsync("go");

        var result = (ToolResultBlock)parentTransport.Requests[1].Messages[^1].Content.Single();
        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Content).Contains("stopped with MaxTokens");
    }

    [Test]
    public async Task Safety_flags_compose_conservatively()
    {
        var (plain, _) = CreateSubAgent(SampleTools.EchoTool);
        var (reader, _) = CreateSubAgent(SampleTools.ReadPageTool);
        var (payer, _) = CreateSubAgent(SampleTools.SendPaymentTool);

        var plainTool = plain.AsTool("plain", "d");
        await Assert.That(plainTool.Untrusted).IsFalse();
        await Assert.That(plainTool.Privileged).IsFalse();

        await Assert.That(reader.AsTool("reader", "d").Untrusted).IsTrue();
        await Assert.That(payer.AsTool("payer", "d").Privileged).IsTrue();
    }

    [Test]
    public async Task Taint_flows_through_the_sub_agent_boundary()
    {
        // The sub-agent CAN read untrusted content, so its output taints the parent -
        // and the parent's privileged tool is then blocked.
        var (reader, readerTransport) = CreateSubAgent(SampleTools.ReadPageTool);
        readerTransport.EnqueueTurn(FakeTransport.TextTurn("summary of the page"));

        var parentOptions = new AgentOptions
        {
            Tools = { reader.AsTool("researcher", "d"), SampleTools.SendPaymentTool },
        };
        var parentTransport = new FakeTransport();
        parentTransport.EnqueueTurn(FakeTransport.ToolTurn(
            FakeTransport.Use("t1", "researcher", """{"message":"read the page"}""")));
        parentTransport.EnqueueTurn(FakeTransport.ToolTurn(
            FakeTransport.Use("t2", "send_payment", """{"amount":9}""")));
        parentTransport.EnqueueTurn(FakeTransport.TextTurn("done"));
        var parent = new ClaudeAgent(parentOptions, parentTransport);

        var result = await parent.RunAsync("go");

        var blocked = (ToolResultBlock)parentTransport.Requests[2].Messages[^1].Content.Single();
        await Assert.That(blocked.IsError).IsTrue();
        await Assert.That(blocked.Content).Contains("cannot run after untrusted content from tool 'researcher'");
        EmissaryAssert.That(result).Tainted();
    }

    [Test]
    public async Task AsTool_validates_arguments()
    {
        var (subAgent, _) = CreateSubAgent();
        await Assert.That(() => subAgent.AsTool("", "d")).Throws<ArgumentException>();
        await Assert.That(() => subAgent.AsTool("n", "")).Throws<ArgumentException>();
    }

    [Test]
    public async Task WithOutput_uses_the_compile_time_schema()
    {
        var options = new AgentOptions().WithOutput<WeatherReport>();

        await Assert.That(options.OutputSchemaJson).IsEqualTo(WeatherReport.JsonSchema);
    }

    [Test]
    public async Task Typed_run_returns_the_deserialized_answer()
    {
        var options = new AgentOptions().WithOutput<WeatherReport>();
        var transport = new FakeTransport();
        transport.EnqueueTurn(FakeTransport.TextTurn("""{"City":"Oslo","TemperatureC":21.5,"Unit":1}"""));
        var agent = new ClaudeAgent(options, transport);

        var report = await agent.RunAsync("weather in oslo", TestJsonContext.Default.WeatherReport);

        await Assert.That(report.City).IsEqualTo("Oslo");
        await Assert.That(report.TemperatureC).IsEqualTo(21.5);
        await Assert.That(report.Unit).IsEqualTo(TemperatureUnit.Fahrenheit);
        await Assert.That((object?)report.Station).IsNull();
    }
}
