using Emissary;

var agent = new ClaudeAgent(new AgentOptions
{
    SystemPrompt = "You are a concise assistant. Use tools when they help.",
    Tools = { HelloTools.RollDiceTool, HelloTools.GetTimeTool },
});

string question = args.Length > 0 ? string.Join(' ', args) : "Roll 3 six-sided dice and tell me the total.";
Console.WriteLine($"> {question}");
Console.WriteLine();

var result = await agent.RunAsync(question);

Console.WriteLine(result.FinalText);
Console.WriteLine();
Console.WriteLine($"[{result.StopReason}; {result.Usage.InputTokens} in / {result.Usage.OutputTokens} out]");
return 0;

internal static partial class HelloTools
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

    /// <summary>Gets the current local time as HH:mm:ss.</summary>
    [ClaudeTool]
    public static string GetTime() =>
        DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
}
