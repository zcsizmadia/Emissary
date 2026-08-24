using Emissary;

var agent = new ClaudeAgent(new AgentOptions
{
    // Capped so a demo run costs a fraction of a cent, and a stuck run stops (SampleBudget).
    Model = SampleBudget.Model,
    // Adaptive thinking is rejected by the small model, so ask for none.
    Thinking = ThinkingMode.Disabled,
    MaxTurns = SampleBudget.MaxTurns,
    TokenBudget = SampleBudget.TokenBudget,
    SystemPrompt =
        "You are a concise assistant. You cannot roll dice or read the clock yourself — always " +
        "use the provided tools for those, then report the actual results they return.",
    Tools = { HelloTools.RollDiceTool, HelloTools.GetTimeTool },
});

string question = args.Length > 0 ? string.Join(' ', args) : "Roll 3 six-sided dice and tell me the total.";
Console.WriteLine($"> {question}");
Console.WriteLine();

// Stream the run so the tool calls are visible — the whole point of the sample.
AgentResult? result = null;
await foreach (var agentEvent in agent.StreamAsync(question))
{
    switch (agentEvent)
    {
        case AgentToolCallEvent call:
            Console.WriteLine($"[tool] {call.Name}");
            break;
        case AgentToolResultEvent toolResult:
            Console.WriteLine($"       -> {toolResult.Result}");
            break;
        case AgentTextEvent text:
            Console.Write(text.Delta);
            break;
        case AgentCompletedEvent completed:
            result = completed.Result;
            break;
        default:
            break;
    }
}

Console.WriteLine();
Console.WriteLine();
Console.WriteLine($"[{result!.StopReason}; {result.Usage.InputTokens} in / {result.Usage.OutputTokens} out]");
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
