# Tools and schemas

Everything on this page happens at **compile time**. There is no reflection in the tool
pipeline, which is why a full agent publishes as a ~1.5 MB Native AOT binary.

## What the generator produces

For each `[ClaudeTool]` method it emits, on the containing partial type:

- a `{MethodName}Tool` property of type `ToolDefinition`,
- the tool's **JSON Schema**, derived from the parameter list,
- a **typed dispatcher** that binds the model's JSON arguments to your parameters and converts
  the return value back to text.

The wire name is the method name in `snake_case` (`GetWeather` → `get_weather`); override it
with `[ClaudeTool(Name = "...")]`.

## Descriptions

Claude picks tools — and argument values — from descriptions, so they matter. Emissary reads
them from your XML doc comments, with the attribute as an override:

```csharp
/// <summary>Refunds a payment for an order.</summary>
/// <param name="orderId">The order id, e.g. ORD-7.</param>
/// <param name="amount">The refund amount in the order's currency.</param>
[ClaudeTool]
public static string RefundPayment(string orderId, double amount) => /* ... */;
```

> [!IMPORTANT]
> Doc comments are only visible to the generator when the project sets
> `<GenerateDocumentationFile>true</GenerateDocumentationFile>`. Without it you'll get `EMS001`
> (no tool description) even though the comment is right there.

## Supported parameter types

`string`, `bool`, `int`, `long`, `double`, enums, arrays of those, and records/classes composed
of them (nested, recursively). `CancellationToken` is injected and excluded from the schema.
Optional parameters with defaults become non-required schema properties.

```csharp
[ClaudeTool(Description = "Books a room.")]
public static string Book(Reservation reservation, bool notify = true) => /* ... */;

public sealed record Reservation(string Room, string CheckInDate, string[] Guests);
```

Anything else is a build error (`EMS002`) naming the offending parameter — not a runtime
serialization failure.

## Capping tool output

A tool that returns far more than you expected — a table dump, a whole log file — quietly
consumes your context window and your token budget. Cap it at the source:

```csharp
/// <summary>Dumps recent rows from a table.</summary>
/// <param name="table">The table name.</param>
[ClaudeTool(MaxResultLength = 8_000)]
public static string DumpTable(string table) => /* ... */;
```

Output past the cap is replaced with a short notice telling the model that data was withheld and
to narrow its request, so it adapts instead of reasoning over a silently truncated answer. A
negative cap is a build error (`EMS011`); `0` (the default) means no cap.

## Structured outputs

Mark a record `[ClaudeSchema]` to get a compile-time **strict** schema
(`additionalProperties: false` at every level), then let the API guarantee the shape:

```csharp
/// <summary>A triaged support ticket.</summary>
/// <param name="Title">A short, specific title.</param>
/// <param name="Severity">How urgent the issue is.</param>
[ClaudeSchema]
public sealed partial record TicketTriage(string Title, Severity Severity, string[] Tags);

var options = new AgentOptions().WithOutput<TicketTriage>();
var triage = await agent.RunAsync("Triage this report: …", MyJsonContext.Default.TicketTriage);
```

Deserialization goes through System.Text.Json source generation, so the whole path — schema,
API, result — is reflection-free.

### Streaming a structured answer

For a UI that should fill in as the model writes, stream the value instead of awaiting it:

```csharp
await foreach (var partial in agent.StreamAsync("Triage this…", MyJsonContext.Default.TicketTriage))
{
    Render(partial);   // title appears first, then severity, then tags
}
```

Emissary completes the partially received JSON on each chunk, so you get a real
`TicketTriage` rather than raw text. Chunks that are not yet deserializable — a half-written
property name, a partially spelled enum — are skipped.

> [!IMPORTANT]
> A partial is a **progress snapshot, not a validated value**: properties that have not arrived
> yet are `null`/`default` *even where the type declares them non-nullable*, and a string may
> hold only the part received so far. Guard against nulls when rendering, and use the final item
> (or `RunAsync<T>`) when you need the whole answer.

## Diagnostics

| Id | Severity | Meaning |
|---|---|---|
| `EMS001` | Warning | Tool has no description |
| `EMS002` | Error | Unsupported parameter or member type |
| `EMS003` | Info | Tool parameter has no `<param>` description |
| `EMS004` | Error | Containing type is not `partial` |
| `EMS005` | Error | Tool method is not `static` |
| `EMS006` | Error | Unsupported return type |
| `EMS007` | Error | Generic tool method or generic containing type |
| `EMS008` | Error | `[ClaudeSchema]` type is not schema-representable |
| `EMS009` | Error | `CompensatedBy` target is not a `[ClaudeTool]` on the same type |
| `EMS010` | Warning | `[AuthorizeTool]` without `[ClaudeTool]` — the policy would be ignored |
| `EMS011` | Error | `MaxResultLength` is negative |

## Composing agents

An agent can be handed to another agent as a single tool, and safety composes with it: if the
sub-agent can read untrusted content, its tool is marked untrusted too, so the parent's taint
rules still apply across the boundary.

```csharp
var researcher = new ClaudeAgent(researchOptions);
parentOptions.Tools.Add(researcher.AsTool("researcher", "Delegates research questions."));
```
