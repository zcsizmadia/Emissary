using Emissary;

namespace SupportDesk;

/// <summary>
/// The offline demo: replays the bundled support-desk trajectory through the exact agent
/// configuration the web host uses, so every safety feature is exercised with zero network.
/// </summary>
internal static class ReplayDemo
{
    public static async Task RunAsync()
    {
        var options = new AgentOptions();
        SupportAgent.Configure(options);

        var trajectory = Trajectory.Load(Path.Combine(AppContext.BaseDirectory, "support.trajectory"));
        var agent = new ClaudeAgent(options, trajectory);

        const string request =
            "Hi, order ORD-7 arrived damaged - please refund the $129.99. Also, where is my other order ORD-9?";
        Console.WriteLine($"customer> {request}\n");

        var suspended = await StreamAsync(agent.StreamAsync(request));
        Console.WriteLine($"\n[run suspended: {suspended.PlannedApprovalTools()} awaiting human approval]\n");
        Console.WriteLine("supervisor> approve the refund\n");

        var final = await StreamAsync(agent.ResumeStreamAsync(suspended.Suspension!, approve: true));

        Console.WriteLine();
        Console.WriteLine($"stop reason : {final.StopReason}");
        Console.WriteLine($"tainted     : {final.Tainted}  (an injection entered via the tracking page)");
        Console.WriteLine($"tokens      : {final.Usage.InputTokens} in / {final.Usage.OutputTokens} out");
        Console.WriteLine($"cache reads : {final.Usage.CacheReadInputTokens} tokens served from cache " +
            $"(of {final.Usage.InputTokens + final.Usage.CacheReadInputTokens} total input)");
    }

    private static async Task<AgentResult> StreamAsync(IAsyncEnumerable<AgentEvent> events)
    {
        AgentResult? result = null;
        await foreach (var agentEvent in events)
        {
            switch (agentEvent)
            {
                case AgentToolCallEvent call:
                    Console.WriteLine($"  [tool] {call.Name}");
                    break;
                case AgentToolResultEvent toolResult:
                    Console.WriteLine($"         -> {(toolResult.IsError ? "BLOCKED: " : "")}{Truncate(toolResult.Result)}");
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

        return result!;
    }

    private static string PlannedApprovalTools(this AgentResult result) =>
        string.Join(", ", result.Suspension!.PendingApprovals.Select(p => p.ToolName));

    private static string Truncate(string text) => text.Length <= 120 ? text : text[..117] + "...";
}
