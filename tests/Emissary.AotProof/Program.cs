using System.Text.Json;
using Emissary;
using Emissary.AotProof;

int failures = 0;

await Check(AotTools.EchoTool, """{"text":"aot"}""", "aot");
await Check(AotTools.AddTool, """{"left":5}""", "15");
await Check(AotTools.DescribeTool, """{"color":"Green","tags":["fast","native"]}""", "Green:fast+native");
await Check(AotTools.ShipTool, """{"order":{"id":"A1","address":{"city":"Oslo","zip":"0150"}}}""", "A1->Oslo/0150 x1");

// An instance tool holding a dependency: proves the this-bound dispatcher is trim-safe too.
var greeter = new GreetingTools("hei");
await Check(greeter.GreetTool, """{"name":"Ada"}""", "hei, Ada");

if (!Verdict.JsonSchema.Contains("\"additionalProperties\":false", StringComparison.Ordinal))
{
    Console.WriteLine("MISMATCH schema: strict marker missing.");
    failures++;
}
else
{
    Console.WriteLine("ok schema: strict");
}

if (failures > 0)
{
    Console.WriteLine($"FAILED: {failures} tool check(s) failed.");
    return 1;
}

Console.WriteLine("All AOT tool checks passed.");
return 0;

async Task Check(ToolDefinition tool, string inputJson, string expected)
{
    using var document = JsonDocument.Parse(inputJson);
    string actual = await tool.InvokeAsync(document.RootElement);
    if (actual != expected)
    {
        Console.WriteLine($"MISMATCH {tool.Name}: expected '{expected}', got '{actual}'");
        failures++;
    }
    else
    {
        Console.WriteLine($"ok {tool.Name}: {actual}");
    }
}

namespace Emissary.AotProof
{
    internal enum Color
    {
        Red,
        Green,
    }

    internal static partial class AotTools
    {
        [ClaudeTool(Description = "Echoes text.")]
        public static string Echo(string text) => text;

        [ClaudeTool(Description = "Adds integers.")]
        public static int Add(int left, int right = 10) => left + right;

        [ClaudeTool(Description = "Describes a color with tags.")]
        public static async Task<string> Describe(Color color, string[] tags, CancellationToken cancellationToken)
        {
            await Task.Delay(1, cancellationToken);
            return color + ":" + string.Join("+", tags);
        }

        /// <summary>Ships an order.</summary>
        /// <param name="order">The order to ship.</param>
        [ClaudeTool]
        public static string Ship(Order order) =>
            $"{order.Id}->{order.Address.City}/{order.Address.Zip} x{order.Quantity}";
    }

    /// <summary>Tools that carry a dependency, so they cannot be static.</summary>
    internal sealed partial class GreetingTools(string greeting)
    {
        /// <summary>Greets someone.</summary>
        /// <param name="name">Who to greet.</param>
        [ClaudeTool]
        public string Greet(string name) => $"{greeting}, {name}";
    }

    internal sealed record Address(string City, string Zip);

    internal sealed record Order(string Id, Address Address, int Quantity = 1);

    [ClaudeSchema]
    internal sealed partial record Verdict(string Summary, bool Approved);
}
