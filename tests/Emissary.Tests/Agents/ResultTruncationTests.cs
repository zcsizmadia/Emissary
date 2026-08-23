using System.Text.Json;
using Emissary.Tests.Agents;
using Emissary.Tests.Tools;

namespace Emissary.Tests;

public sealed class ResultTruncationTests
{
    [Test]
    public async Task Output_within_the_cap_is_untouched()
    {
        await Assert.That(ToolResultTruncation.Apply("short", 50)).IsEqualTo("short");
        await Assert.That(ToolResultTruncation.Apply(new string('a', 50), 50)).IsEqualTo(new string('a', 50));
    }

    [Test]
    public async Task Output_past_the_cap_keeps_the_head_and_explains_itself()
    {
        string truncated = ToolResultTruncation.Apply(new string('a', 1500), 10);

        await Assert.That(truncated).StartsWith(new string('a', 10));
        await Assert.That(truncated).Contains("truncated: 1,490 of 1,500 characters omitted");
        await Assert.That(truncated).Contains("narrow the request");
    }

    [Test]
    public async Task The_agent_truncates_a_capped_tool_result()
    {
        var options = new AgentOptions { Tools = { SampleTools.DumpTableTool } };
        var transport = new FakeTransport();
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "dump_table", """{"table":"orders"}""")));
        transport.EnqueueTurn(FakeTransport.TextTurn("summarized"));
        var agent = new ClaudeAgent(options, transport);

        await agent.RunAsync("dump the orders table");

        var result = (ToolResultBlock)transport.Requests[1].Messages[^1].Content.Single();
        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Content).StartsWith(new string('x', 50));
        await Assert.That(result.Content).Contains("450 of 500 characters omitted");
    }

    [Test]
    public async Task Uncapped_tools_are_unaffected()
    {
        var options = new AgentOptions { Tools = { SampleTools.EchoTool } };
        var transport = new FakeTransport();
        string big = new('y', 5000);
        transport.EnqueueTurn(FakeTransport.ToolTurn(
            FakeTransport.Use("t1", "echo", JsonSerializer.Serialize(new Dictionary<string, string> { ["text"] = big }))));
        transport.EnqueueTurn(FakeTransport.TextTurn("done"));
        var agent = new ClaudeAgent(options, transport);

        await agent.RunAsync("echo a lot");

        var result = (ToolResultBlock)transport.Requests[1].Messages[^1].Content.Single();
        await Assert.That(result.Content.Length).IsEqualTo(5000);
    }

    [Test]
    public async Task Generated_tools_carry_the_cap()
    {
        await Assert.That(SampleTools.DumpTableTool.MaxResultLength).IsEqualTo(50);
        await Assert.That(SampleTools.EchoTool.MaxResultLength).IsNull();
    }

    [Test]
    public async Task A_non_positive_cap_is_rejected_at_construction()
    {
        await Assert.That(() => new ToolDefinition(
                "t", "d", """{"type":"object","properties":{}}""",
                (_, _) => new ValueTask<string>("x"), maxResultLength: 0))
            .Throws<ArgumentOutOfRangeException>();
    }
}
