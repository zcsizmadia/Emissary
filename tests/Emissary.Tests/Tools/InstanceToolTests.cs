using Emissary.Tests.Agents;
using Emissary.Tests.Generators;

namespace Emissary.Tests;

/// <summary>A stand-in for an injected dependency — a repository, an HttpClient, a DbContext.</summary>
public sealed class OrderBook
{
    private readonly Dictionary<string, string> _statuses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A-1001"] = "shipped",
    };

    public List<string> Cancelled { get; } = [];

    public string StatusOf(string orderId) =>
        _statuses.TryGetValue(orderId, out string? status) ? status : "unknown";

    public void Cancel(string orderId) => Cancelled.Add(orderId);
}

/// <summary>Tools that need a dependency, so they cannot be static.</summary>
public sealed partial class OrderTools(OrderBook orders)
{
    /// <summary>Looks up the status of an order.</summary>
    /// <param name="orderId">The order id.</param>
    [ClaudeTool(CompensatedBy = nameof(CancelOrder))]
    public string LookupOrder(string orderId) => orders.StatusOf(orderId);

    /// <summary>Cancels an order.</summary>
    /// <param name="orderId">The order id.</param>
    [ClaudeTool(Privileged = true)]
    public string CancelOrder(string orderId)
    {
        orders.Cancel(orderId);
        return $"cancelled {orderId}";
    }
}

public sealed class InstanceToolTests
{
    [Test]
    public async Task An_instance_tool_runs_against_its_dependency()
    {
        var orders = new OrderBook();
        var tools = new OrderTools(orders);
        var options = new AgentOptions();
        options.Tools.Add(tools.LookupOrderTool);
        var transport = new FakeTransport();
        transport.EnqueueTurn(FakeTransport.ToolTurn(
            FakeTransport.Use("t1", "lookup_order", """{"order_id":"A-1001"}""")));
        transport.EnqueueTurn(FakeTransport.TextTurn("It shipped."));

        var result = await new ClaudeAgent(options, transport).RunAsync("where is A-1001?");

        var toolResult = (ToolResultBlock)transport.Requests[1].Messages[^1].Content.Single();
        await Assert.That(toolResult.Content).IsEqualTo("shipped");
        await Assert.That(result.FinalText).IsEqualTo("It shipped.");
    }

    [Test]
    public async Task Each_instance_carries_its_own_dependency()
    {
        var first = new OrderTools(new OrderBook());
        var second = new OrderTools(new OrderBook());

        await Assert.That(first.LookupOrderTool).IsNotSameReferenceAs(second.LookupOrderTool);
        await Assert.That(first.LookupOrderTool.Name).IsEqualTo(second.LookupOrderTool.Name);
    }

    [Test]
    public async Task The_definition_is_built_once_per_instance()
    {
        var tools = new OrderTools(new OrderBook());

        // Repeated access returns the same definition, so adding it to options never re-schematizes.
        await Assert.That(tools.LookupOrderTool).IsSameReferenceAs(tools.LookupOrderTool);
    }

    [Test]
    public async Task Schema_descriptions_and_safety_flags_work_the_same_as_for_static_tools()
    {
        var tools = new OrderTools(new OrderBook());

        await Assert.That(tools.LookupOrderTool.Description).IsEqualTo("Looks up the status of an order.");
        await Assert.That(tools.LookupOrderTool.InputSchemaJson).Contains("\"The order id.\"");
        await Assert.That(tools.LookupOrderTool.Privileged).IsFalse();
        await Assert.That(tools.CancelOrderTool.Privileged).IsTrue();
    }

    [Test]
    public async Task Compensation_unwinds_through_the_same_instance()
    {
        var orders = new OrderBook();
        var tools = new OrderTools(orders);
        var options = new AgentOptions();
        options.Tools.Add(tools.LookupOrderTool);
        var transport = new FakeTransport();
        transport.EnqueueTurn(FakeTransport.ToolTurn(
            FakeTransport.Use("t1", "lookup_order", """{"order_id":"A-1001"}""")));
        transport.EnqueueTurn(FakeTransport.TextTurn("done"));
        var agent = new ClaudeAgent(options, transport);

        var result = await agent.RunAsync("check A-1001");
        var report = await agent.CompensateAsync(result);

        await Assert.That(report.Single().Success).IsTrue();
        await Assert.That(orders.Cancelled).IsEquivalentTo(["A-1001"]);
    }

    [Test]
    public async Task An_instance_tool_generates_an_instance_property_and_a_bound_dispatcher()
    {
        var result = await GeneratorHarness.RunClean("""
            public partial class Tools
            {
                private readonly string _prefix = "p";

                [Emissary.ClaudeTool(Description = "d")]
                public string Echo(string text) => _prefix + text;
            }
            """);

        string source = GeneratorHarness.GeneratedSource(result);
        await Assert.That(source).Contains("private global::Emissary.ToolDefinition? __emissaryTool_Echo;");
        await Assert.That(source).Contains(
            "public global::Emissary.ToolDefinition EchoTool => __emissaryTool_Echo ??= new global::Emissary.ToolDefinition(");
        await Assert.That(source).Contains(
            "private global::System.Threading.Tasks.ValueTask<string> __EmissaryInvoke_Echo(");
        await Assert.That(source).DoesNotContain("private static global::System.Threading.Tasks.ValueTask<string> __EmissaryInvoke_Echo(");
    }

    [Test]
    public async Task A_static_tool_still_generates_a_static_property()
    {
        var result = await GeneratorHarness.RunClean("""
            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d")]
                public static string Echo(string text) => text;
            }
            """);

        string source = GeneratorHarness.GeneratedSource(result);
        await Assert.That(source).Contains("public static global::Emissary.ToolDefinition EchoTool { get; } =");
        await Assert.That(source).DoesNotContain("__emissaryTool_Echo");
    }

    [Test]
    public async Task Instance_tools_work_in_records_and_structs()
    {
        var result = await GeneratorHarness.RunClean("""
            public partial record struct Holder(string Prefix)
            {
                [Emissary.ClaudeTool(Description = "d")]
                public string Echo(string text) => Prefix + text;
            }
            """);

        await Assert.That(GeneratorHarness.GeneratedSource(result)).Contains("partial record struct Holder");
    }
}
