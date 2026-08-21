using Emissary.Testing;
using Emissary.Tests.Agents;
using Emissary.Tests.Tools;
using Emissary.Transport;

namespace Emissary.Tests;

public sealed class ContractsAndSafetyTests
{
    private static (ClaudeAgent Agent, FakeTransport Transport) Create(Action<AgentOptions> configure)
    {
        var options = new AgentOptions();
        options.Tools.Add(SampleTools.EchoTool);
        options.Tools.Add(SampleTools.AddTool);
        configure(options);
        var transport = new FakeTransport();
        return (new ClaudeAgent(options, transport), transport);
    }

    private static ToolResultBlock Result(FakeTransport transport, int requestIndex, int blockIndex = 0) =>
        (ToolResultBlock)transport.Requests[requestIndex].Messages[^1].Content[blockIndex];

    [Test]
    public async Task Require_blocks_until_prerequisite_succeeds()
    {
        var (agent, transport) = Create(options => options.Rules.Require("add", "echo"));
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "add", """{"left":1}""")));
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t2", "echo", """{"text":"ok"}""")));
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t3", "add", """{"left":1}""")));
        transport.EnqueueTurn(FakeTransport.TextTurn("done"));

        await agent.RunAsync("go");

        await Assert.That(Result(transport, 1).IsError).IsTrue();
        await Assert.That(Result(transport, 1).Content).Contains("requires a prior successful call to 'echo'");
        await Assert.That(Result(transport, 3).IsError).IsFalse();
    }

    [Test]
    public async Task Failed_prerequisite_does_not_unlock_the_guarded_tool()
    {
        var (agent, transport) = Create(options => options.Rules.Require("add", "echo"));
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "echo", "{}")));
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t2", "add", """{"left":1}""")));
        transport.EnqueueTurn(FakeTransport.TextTurn("done"));

        await agent.RunAsync("go");

        await Assert.That(Result(transport, 1).IsError).IsTrue();
        await Assert.That(Result(transport, 2).IsError).IsTrue();
        await Assert.That(Result(transport, 2).Content).Contains("requires a prior successful");
    }

    [Test]
    public async Task Terminal_tool_ends_all_tool_calling()
    {
        var (agent, transport) = Create(options => options.Rules.Terminal("echo"));
        transport.EnqueueTurn(FakeTransport.ToolTurn(
            FakeTransport.Use("t1", "echo", """{"text":"bye"}"""),
            FakeTransport.Use("t2", "add", """{"left":1}""")));
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t3", "add", """{"left":2}""")));
        transport.EnqueueTurn(FakeTransport.TextTurn("done"));

        await agent.RunAsync("go");

        await Assert.That(Result(transport, 1, 0).IsError).IsFalse();
        await Assert.That(Result(transport, 1, 1).Content).Contains("after terminal tool 'echo'");
        await Assert.That(Result(transport, 2).Content).Contains("after terminal tool 'echo'");
    }

    [Test]
    public async Task Limit_caps_calls_within_and_across_batches()
    {
        var (agent, transport) = Create(options => options.Rules.Limit("echo", 1));
        transport.EnqueueTurn(FakeTransport.ToolTurn(
            FakeTransport.Use("t1", "echo", """{"text":"a"}"""),
            FakeTransport.Use("t2", "echo", """{"text":"b"}""")));
        transport.EnqueueTurn(FakeTransport.TextTurn("done"));

        await agent.RunAsync("go");

        await Assert.That(Result(transport, 1, 0).IsError).IsFalse();
        await Assert.That(Result(transport, 1, 1).Content).Contains("exceeded its limit of 1 call(s)");
    }

    [Test]
    public async Task Untrusted_output_taints_the_run_and_blocks_privileged_tools()
    {
        var (agent, transport) = Create(options =>
        {
            options.Tools.Add(SampleTools.ReadPageTool);
            options.Tools.Add(SampleTools.SendPaymentTool);
        });
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "read_page", """{"url":"http://evil"}""")));
        transport.EnqueueTurn(FakeTransport.ToolTurn(
            FakeTransport.Use("t2", "send_payment", """{"amount":100}"""),
            FakeTransport.Use("t3", "echo", """{"text":"still fine"}""")));
        transport.EnqueueTurn(FakeTransport.TextTurn("done"));

        var result = await agent.RunAsync("go");

        await Assert.That(Result(transport, 2, 0).IsError).IsTrue();
        await Assert.That(Result(transport, 2, 0).Content)
            .Contains("privileged tool 'send_payment' cannot run after untrusted content from tool 'read_page'");
        await Assert.That(Result(transport, 2, 1).IsError).IsFalse();
        EmissaryAssert.That(result).Tainted();
    }

    [Test]
    public async Task Privileged_tool_runs_normally_in_untainted_runs()
    {
        var (agent, transport) = Create(options => options.Tools.Add(SampleTools.SendPaymentTool));
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "send_payment", """{"amount":5}""")));
        transport.EnqueueTurn(FakeTransport.TextTurn("done"));

        var result = await agent.RunAsync("go");

        await Assert.That(Result(transport, 1).IsError).IsFalse();
        EmissaryAssert.That(result).NotTainted();
    }

    [Test]
    public async Task Policy_gated_tool_is_invisible_and_unexecutable_without_authorization()
    {
        var (agent, transport) = Create(options => options.Tools.Add(SampleTools.DeleteDataTool));
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "delete_data", """{"id":"x"}""")));
        transport.EnqueueTurn(FakeTransport.TextTurn("done"));

        await agent.RunAsync("go");

        await Assert.That(transport.Requests[0].Tools.Select(t => t.Name)).DoesNotContain("delete_data");
        await Assert.That(Result(transport, 1).Content).IsEqualTo("Unknown tool 'delete_data'.");
    }

    [Test]
    public async Task Granted_policy_exposes_and_executes_the_tool()
    {
        var (agent, transport) = Create(options =>
        {
            options.Tools.Add(SampleTools.DeleteDataTool);
            options.Authorizer = new PolicyToolAuthorizer("admin", "other");
        });
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "delete_data", """{"id":"x"}""")));
        transport.EnqueueTurn(FakeTransport.TextTurn("done"));

        await agent.RunAsync("go");

        await Assert.That(transport.Requests[0].Tools.Select(t => t.Name)).Contains("delete_data");
        await Assert.That(Result(transport, 1).Content).IsEqualTo("deleted x");
    }

    [Test]
    public async Task Generated_tools_carry_policy_and_flags()
    {
        await Assert.That(SampleTools.DeleteDataTool.RequiredPolicy).IsEqualTo("admin");
        await Assert.That(SampleTools.ReadPageTool.Untrusted).IsTrue();
        await Assert.That(SampleTools.SendPaymentTool.Privileged).IsTrue();
        await Assert.That(SampleTools.EchoTool.RequiredPolicy).IsNull();
        await Assert.That(SampleTools.EchoTool.Untrusted).IsFalse();
        await Assert.That(SampleTools.EchoTool.Privileged).IsFalse();
    }

    [Test]
    public async Task Authorizer_helper_validates_and_grants()
    {
        await Assert.That(() => new PolicyToolAuthorizer(null!)).Throws<ArgumentNullException>();
        var authorizer = new PolicyToolAuthorizer("a");
        await Assert.That(() => authorizer.IsAuthorized(null!)).Throws<ArgumentNullException>();
        await Assert.That(authorizer.IsAuthorized(SampleTools.EchoTool)).IsTrue();
        await Assert.That(authorizer.IsAuthorized(SampleTools.DeleteDataTool)).IsFalse();
    }

    [Test]
    public async Task Authorize_attribute_carries_its_policy()
    {
        await Assert.That(new AuthorizeToolAttribute("refunds").Policy).IsEqualTo("refunds");
    }

    [Test]
    public async Task Rules_validate_their_arguments()
    {
        var rules = new ToolRules();
        await Assert.That(() => rules.Require("", "x")).Throws<ArgumentException>();
        await Assert.That(() => rules.Require("x", "")).Throws<ArgumentException>();
        await Assert.That(() => rules.Terminal("")).Throws<ArgumentException>();
        await Assert.That(() => rules.Limit("", 1)).Throws<ArgumentException>();
        await Assert.That(() => rules.Limit("x", 0)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Taint_assertions_fail_in_both_directions()
    {
        var (agent, transport) = Create(_ => { });
        transport.EnqueueTurn(FakeTransport.TextTurn("clean"));
        var clean = await agent.RunAsync("go");

        await Assert.That(() => EmissaryAssert.That(clean).Tainted())
            .Throws<EmissaryAssertionException>();

        var (taintedAgent, taintedTransport) = Create(options => options.Tools.Add(SampleTools.ReadPageTool));
        taintedTransport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "read_page", """{"url":"u"}""")));
        taintedTransport.EnqueueTurn(FakeTransport.TextTurn("done"));
        var tainted = await taintedAgent.RunAsync("go");

        await Assert.That(() => EmissaryAssert.That(tainted).NotTainted())
            .Throws<EmissaryAssertionException>();
    }
}
