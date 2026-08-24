using System.Text.Json;
using Emissary;
using Microsoft.Extensions.DependencyInjection;

bool haveKey = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));

// Tools that need a dependency — a repository, an HttpClient, a DbContext — are instance methods,
// so the tool object comes from the container and its lifetime is the container's business.
var services = new ServiceCollection();
services.AddSingleton<OrderStore>();                 // stands in for a database
services.AddScoped<TenantContext>();                 // per-request state
services.AddScoped<OrderTools>();                    // the tools themselves

using var provider = services.BuildServiceProvider();

// Two "requests", each in its own scope, to show that each run gets its own tool instance and
// therefore its own scoped dependencies.
foreach (var (tenant, question) in new[]
{
    ("acme", "What is the status of order A-1001, and when did it ship?"),
    ("globex", "Has order G-7 been delivered?"),
})
{
    using var scope = provider.CreateScope();
    scope.ServiceProvider.GetRequiredService<TenantContext>().Name = tenant;
    var tools = scope.ServiceProvider.GetRequiredService<OrderTools>();

    Console.WriteLine($"=== tenant '{tenant}' (tool instance #{tools.InstanceId}) ===");

    // The generated definition is just a delegate bound to this instance, so it can be called
    // directly — no API key needed. This is also why injected tools stay unit-testable.
    using var probe = JsonDocument.Parse("""{"order_id":"A-1001"}""");
    Console.WriteLine($"[direct] lookup_order(A-1001) -> {await tools.LookupOrderTool.InvokeAsync(probe.RootElement)}");

    if (!haveKey)
    {
        Console.WriteLine("[skipped] Set ANTHROPIC_API_KEY to run the agent itself.");
        Console.WriteLine();
        continue;
    }

    Console.WriteLine($"> {question}");

    var options = new AgentOptions
    {
        // Capped so a demo run costs a fraction of a cent, and a stuck run stops (SampleBudget).
        Model = SampleBudget.Model,
        // Adaptive thinking is rejected by the small model, so ask for none.
        Thinking = ThinkingMode.Disabled,
        MaxTurns = SampleBudget.MaxTurns,
        TokenBudget = SampleBudget.TokenBudget,
        SystemPrompt =
            "You are an order-support agent. Look orders up with the tool rather than guessing, " +
            "and answer in one or two sentences.",
        // The generated property is an *instance* property here, bound to this scope's tools.
        Tools = { tools.LookupOrderTool },
    };
    options.ToolFailures.Timeout = TimeSpan.FromSeconds(10);

    var agent = new ClaudeAgent(options);
    var result = await agent.RunAsync(question);

    Console.WriteLine(result.FinalText);
    foreach (var failure in result.ToolFailures)
    {
        Console.WriteLine($"[tool failed] {failure.ToolName}: {failure.Exception.Message}");
    }

    Console.WriteLine($"[{result.StopReason}; {result.Usage.InputTokens} in / {result.Usage.OutputTokens} out]");
    Console.WriteLine();
}

return 0;

/// <summary>Per-scope state, the kind of thing a static tool method cannot reach.</summary>
internal sealed class TenantContext
{
    public string Name { get; set; } = "unknown";
}

/// <summary>Stands in for a database or an upstream service.</summary>
internal sealed class OrderStore
{
    private readonly Dictionary<(string Tenant, string Order), string> _orders = new()
    {
        [("acme", "A-1001")] = "shipped on 2026-08-19 via DHL, tracking JD0002",
        [("globex", "G-7")] = "delivered on 2026-08-14, signed for by M. Rivera",
    };

    public string? Lookup(string tenant, string orderId) =>
        _orders.TryGetValue((tenant, orderId), out string? status) ? status : null;
}

/// <summary>
/// Tools with dependencies. Because the methods are instance methods, the generator emits instance
/// <c>{Method}Tool</c> properties whose handlers are bound to this object — no static state, no
/// service locator, and the tools are as testable as any other injected class.
/// </summary>
internal sealed partial class OrderTools(OrderStore store, TenantContext tenant)
{
    private static int _created;

    public int InstanceId { get; } = Interlocked.Increment(ref _created);

    /// <summary>Looks up the delivery status of an order for the current tenant.</summary>
    /// <param name="orderId">The order id, for example A-1001.</param>
    [ClaudeTool]
    public string LookupOrder(string orderId) =>
        store.Lookup(tenant.Name, orderId)
            ?? $"No order '{orderId}' exists for {tenant.Name}.";
}
