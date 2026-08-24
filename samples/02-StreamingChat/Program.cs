using Emissary;

var agent = new ClaudeAgent(new AgentOptions
{
    // Capped so a demo run costs a fraction of a cent, and a stuck run stops (SampleBudget).
    Model = SampleBudget.Model,
    // Adaptive thinking is rejected by the small model, so ask for none.
    Thinking = ThinkingMode.Disabled,
    MaxTurns = SampleBudget.MaxTurns,
    TokenBudget = SampleBudget.TokenBudget,
    SystemPrompt = "You are a friendly, concise chat assistant.",
    Effort = EffortLevel.Medium,
});

Console.WriteLine("Streaming chat with Claude — thinking shown dim. Empty line to exit.");
var conversation = Conversation.Start();

while (true)
{
    Console.Write("\nyou> ");
    string? input = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(input))
    {
        break;
    }

    conversation = conversation.Append(Message.User(input));
    Console.Write("claude> ");

    AgentResult? result = null;
    bool inThinking = false;
    await foreach (var agentEvent in agent.StreamAsync(conversation))
    {
        switch (agentEvent)
        {
            case AgentThinkingEvent thinking:
                if (!inThinking)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    inThinking = true;
                }

                Console.Write(thinking.Delta);
                break;
            case AgentTextEvent text:
                if (inThinking)
                {
                    Console.ResetColor();
                    Console.WriteLine();
                    inThinking = false;
                }

                Console.Write(text.Delta);
                break;
            case AgentCompletedEvent completed:
                result = completed.Result;
                break;
            default:
                break;
        }
    }

    Console.ResetColor();
    Console.WriteLine();

    // The result carries the updated immutable conversation — this is the whole
    // multi-turn state management story.
    conversation = result!.Conversation;
}

Console.WriteLine("bye!");
return 0;
