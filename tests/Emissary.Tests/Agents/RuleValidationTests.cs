using Emissary.Tests.Agents;
using Emissary.Tests.Tools;

namespace Emissary.Tests;

/// <summary>
/// A contract that names a tool the agent does not have can never fire, so it is rejected at
/// construction: a typo in a safety rule must not silently leave a privileged tool unguarded.
/// </summary>
public sealed class RuleValidationTests
{
    private static ArgumentException Reject(Action<AgentOptions> configure)
    {
        var options = new AgentOptions();
        configure(options);
        return Assert.Throws<ArgumentException>(() => _ = new ClaudeAgent(options, new FakeTransport()))!;
    }

    [Test]
    public async Task A_misspelled_prerequisite_is_rejected()
    {
        var thrown = Reject(options =>
        {
            options.Tools.Add(SampleTools.EchoTool);
            options.Tools.Add(SampleTools.SendPaymentTool);
            options.Rules.Require("send_payment", prerequisite: "verify_identity");
        });

        await Assert.That(thrown.Message).Contains("names 'verify_identity', which this agent has no tool for");
        await Assert.That(thrown.Message).Contains("Its tools are 'echo', 'send_payment'");
        await Assert.That(thrown.ParamName).IsEqualTo("options");
    }

    [Test]
    public async Task A_misspelled_guarded_tool_is_rejected()
    {
        var thrown = Reject(options =>
        {
            options.Tools.Add(SampleTools.EchoTool);
            options.Rules.Require("send_paymnet", prerequisite: "echo");
        });

        await Assert.That(thrown.Message).Contains("'send_paymnet'");
    }

    [Test]
    public async Task Terminal_and_limit_names_are_checked_too()
    {
        await Assert.That(Reject(options =>
        {
            options.Tools.Add(SampleTools.EchoTool);
            options.Rules.Terminal("clsoe_ticket");
        }).Message).Contains("'clsoe_ticket'");

        await Assert.That(Reject(options =>
        {
            options.Tools.Add(SampleTools.EchoTool);
            options.Rules.Limit("send_emial", 3);
        }).Message).Contains("'send_emial'");
    }

    [Test]
    public async Task Every_unknown_name_is_listed_once_in_order()
    {
        var thrown = Reject(options =>
        {
            options.Tools.Add(SampleTools.EchoTool);
            options.Rules.Terminal("zeta");
            options.Rules.Limit("alpha", 2);
            options.Rules.Require("zeta", prerequisite: "alpha");
        });

        await Assert.That(thrown.Message).Contains("names 'alpha', 'zeta', which this agent has no tool for");
    }

    [Test]
    public async Task An_agent_with_no_tools_says_so()
    {
        var thrown = Reject(options => options.Rules.Terminal("echo"));

        await Assert.That(thrown.Message).Contains("Its tools are (none)");
    }

    [Test]
    public async Task Correct_contracts_still_construct()
    {
        var options = new AgentOptions();
        options.Tools.Add(SampleTools.EchoTool);
        options.Tools.Add(SampleTools.SendPaymentTool);
        options.Rules.Require("send_payment", prerequisite: "echo").Terminal("send_payment").Limit("echo", 3);

        await Assert.That(new ClaudeAgent(options, new FakeTransport())).IsNotNull();
    }

    [Test]
    public async Task A_handoff_tool_may_be_named_by_a_contract()
    {
        var specialist = new ClaudeAgent(new AgentOptions(), new FakeTransport());
        var options = new AgentOptions();
        options.Tools.Add(SampleTools.EchoTool);
        options.Handoffs.Add(new HandoffTarget("billing", specialist, "Billing."));
        options.Rules.Require("handoff_to_billing", prerequisite: "echo");

        await Assert.That(new ClaudeAgent(options, new FakeTransport())).IsNotNull();
    }

    [Test]
    public async Task A_contract_on_a_tool_the_caller_is_not_authorized_for_is_allowed()
    {
        // The tool is declared but filtered out of the prompt by RBAC. The rule is still
        // well-formed — it just cannot fire — so construction must not fail.
        var options = new AgentOptions();
        options.Tools.Add(SampleTools.EchoTool);
        options.Tools.Add(SampleTools.DeleteDataTool);
        options.Rules.Require("delete_data", prerequisite: "echo");

        var agent = new ClaudeAgent(options, new FakeTransport());

        await Assert.That(agent).IsNotNull();
    }
}
