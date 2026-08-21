using Emissary;
using Emissary.Mcp;
using McpServerSample;

var options = new EmissaryMcpServerOptions { Name = "emissary-demo" };

// The generated C# tools become MCP tools directly - they run locally, no API key needed.
options.Tools.Add(DemoTools.RollDiceTool);
options.Tools.Add(DemoTools.ConvertTemperatureTool);

// With an API key, the whole agent is additionally exposed as one MCP tool.
if (Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is { Length: > 0 })
{
    options.Agent = new ClaudeAgent(new AgentOptions
    {
        SystemPrompt = "You are a concise assistant.",
        Tools = { DemoTools.RollDiceTool, DemoTools.ConvertTemperatureTool },
    });
    options.AgentToolName = "ask_emissary";
    options.AgentToolDescription = "Ask the Emissary demo agent; it can roll dice and convert temperatures.";
}

await new EmissaryMcpServer(options).RunAsync(
    Console.OpenStandardInput(),
    Console.OpenStandardOutput());
return 0;

namespace McpServerSample
{
    internal static partial class DemoTools
    {
        /// <summary>Rolls dice and reports each roll and the total.</summary>
        /// <param name="sides">The number of sides per die.</param>
        /// <param name="count">The number of dice to roll.</param>
        [ClaudeTool]
        public static string RollDice(int sides = 6, int count = 1)
        {
            var rolls = new int[count];
            for (int i = 0; i < count; i++)
            {
                rolls[i] = Random.Shared.Next(1, sides + 1);
            }

            return $"rolls: {string.Join(", ", rolls)}; total: {rolls.Sum()}";
        }

        /// <summary>Converts a temperature between Celsius and Fahrenheit.</summary>
        /// <param name="value">The temperature value.</param>
        /// <param name="toFahrenheit">Convert C to F when true; F to C when false.</param>
        [ClaudeTool]
        public static string ConvertTemperature(double value, bool toFahrenheit = true) =>
            toFahrenheit
                ? $"{value}C = {value * 9 / 5 + 32}F"
                : $"{value}F = {(value - 32) * 5 / 9:0.##}C";
    }
}
