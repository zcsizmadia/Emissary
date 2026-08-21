using System.Text.Json;
using Emissary;
using Emissary.AotProof;

int failures = 0;

await Check(AotTools.EchoTool, """{"text":"aot"}""", "aot");
await Check(AotTools.AddTool, """{"left":5}""", "15");
await Check(AotTools.DescribeTool, """{"color":"Green","tags":["fast","native"]}""", "Green:fast+native");

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
    }
}
