# 10 — Injected tools

Tools that need a dependency — a repository, an `HttpClient`, a `DbContext`, per-request state — are
**instance methods**. The generator then emits an instance `{Method}Tool` property whose handler is
bound to that object, so the tool comes out of your container with no static state and no service
locator.

```csharp
internal sealed partial class OrderTools(OrderStore store, TenantContext tenant)
{
    /// <summary>Looks up the delivery status of an order for the current tenant.</summary>
    /// <param name="orderId">The order id, for example A-1001.</param>
    [ClaudeTool]
    public string LookupOrder(string orderId) => store.Lookup(tenant.Name, orderId) ?? "not found";
}
```

```csharp
using var scope = provider.CreateScope();
var tools = scope.ServiceProvider.GetRequiredService<OrderTools>();
var options = new AgentOptions { Tools = { tools.LookupOrderTool } };
```

## What this sample shows

- **Scoped dependencies.** Two "requests" run in separate scopes with different `TenantContext`
  values. Each gets its own `OrderTools` instance, and the printed instance id proves it — the same
  question answers differently per tenant because the tool reads scoped state.
- **Lifetimes stay yours.** Build the options inside the scope the dependencies expect. Emissary
  does not capture or extend anything.
- **Still testable.** The generated `LookupOrderTool` is a delegate bound to the instance, so the
  sample invokes it directly — no API call — which is exactly how you unit-test an injected tool.

## Running it

```bash
dotnet run --project samples/10-InjectedTools
```

The direct tool invocation runs with no configuration. Set `ANTHROPIC_API_KEY` to also run the agent
against Claude; without it that part is skipped and the sample says so.
