# Getting started

## Install

```bash
dotnet add package Emissary --prerelease
export ANTHROPIC_API_KEY=sk-ant-...        # PowerShell: $env:ANTHROPIC_API_KEY = "sk-ant-..."
```

One package brings the runtime, the source generator, and the analyzer. Emissary targets
**.NET 10** and is Native AOT compatible.

## Your first tool

A tool is a static method with an attribute. The generator turns it into a wire-ready tool at
compile time — the JSON Schema comes from the signature, the descriptions from your doc comments:

```csharp
using Emissary;

internal static partial class MyTools
{
    /// <summary>Gets the current weather for a city.</summary>
    /// <param name="city">The city name.</param>
    [ClaudeTool]
    public static string GetWeather(string city) => $"18°C and clear in {city}";
}
```

One rule the compiler enforces for you: the containing type must be `partial`, or you get a build
error (`EMS004`) rather than a runtime surprise. The method can be static, as here, or an instance
method when the tool needs injected dependencies — see
[Tools with dependencies](tools.md#tools-with-dependencies).

## Your first agent

```csharp
var agent = new ClaudeAgent(new AgentOptions
{
    SystemPrompt = "You are a concise assistant.",
    Tools = { MyTools.GetWeatherTool },      // generated: {MethodName}Tool
});

var result = await agent.RunAsync("What's the weather in Oslo?");
Console.WriteLine(result.FinalText);
Console.WriteLine($"{result.Usage.InputTokens} in / {result.Usage.OutputTokens} out");
```

`RunAsync` drives the whole loop: it sends the conversation, executes any tools Claude calls
(in parallel when it calls several), feeds the results back, and repeats until the model
finishes or a limit is hit.

## Streaming

Use `StreamAsync` when you want tokens as they arrive, or want to observe tool calls:

```csharp
await foreach (var e in agent.StreamAsync("What's the weather in Oslo?"))
{
    switch (e)
    {
        case AgentTextEvent text:        Console.Write(text.Delta); break;
        case AgentThinkingEvent think:   Console.Write(think.Delta); break;
        case AgentToolCallEvent call:    Console.WriteLine($"[tool] {call.Name}"); break;
        case AgentToolResultEvent r:     Console.WriteLine($"  -> {r.Result}"); break;
        case AgentCompletedEvent done:   /* done.Result */ break;
    }
}
```

## Multi-turn

Conversations are immutable; carry the result's conversation into the next turn:

```csharp
var first = await agent.RunAsync("My name is Dana.");
var second = await agent.RunAsync(first.Conversation.Append(Message.User("What's my name?")));
```

For chat that must survive restarts, use a [session](production.md#durable-chat-sessions)
instead.

## Dependency injection

```csharp
builder.Services.AddEmissary(options =>
{
    options.SystemPrompt = "You are a concise assistant.";
    options.Tools.Add(MyTools.GetWeatherTool);
});
```

## Where to next

- [Tools and schemas](tools.md) — parameter types, structured outputs, diagnostics
- [Safety and contracts](safety.md) — the guarantees that make an agent auditable
- [Testing agents](testing.md) — record/replay and behavioral assertions
