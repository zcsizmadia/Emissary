using Emissary;
using Emissary.Testing;
using RecordReplay;

var options = new AgentOptions
{
    SystemPrompt = "You are a support agent. Always verify identity before refunding.",
    Tools = { SupportTools.VerifyIdentityTool, SupportTools.RefundPaymentTool },
};

const string request = "Please refund my last payment of $19.99. I'm customer C42.";
bool record = args.Contains("--record", StringComparer.Ordinal);

ClaudeAgent agent;
TrajectoryRecorder? recorder = null;
if (record)
{
    // Live run against the API, recording every exchange.
    recorder = new TrajectoryRecorder();
    agent = new ClaudeAgent(options, recorder);
    Console.WriteLine("Recording a live run...");
}
else
{
    // Deterministic replay of the bundled recording - zero network, no API key needed.
    var trajectory = Trajectory.Load(Path.Combine(AppContext.BaseDirectory, "demo.trajectory"));
    agent = new ClaudeAgent(options, trajectory);
    Console.WriteLine("Replaying the bundled trajectory (zero network)...");
}

Console.WriteLine($"> {request}");
Console.WriteLine();

AgentResult? result = null;
await foreach (var agentEvent in agent.StreamAsync(request))
{
    switch (agentEvent)
    {
        case AgentToolCallEvent call:
            Console.WriteLine($"[tool] {call.Name}");
            break;
        case AgentToolResultEvent toolResult:
            Console.WriteLine($"       -> {toolResult.Result}");
            break;
        case AgentCompletedEvent completed:
            result = completed.Result;
            break;
        default:
            break;
    }
}

Console.WriteLine();
Console.WriteLine(result!.FinalText);

// Provable behavior: the refund never happened before the identity check.
EmissaryAssert.That(result)
    .ToolCalled("verify_identity")
    .ToolCalled("refund_payment", times: 1)
    .ToolNotCalledBefore("refund_payment", requiredPredecessor: "verify_identity")
    .Stopped(AgentStopReason.Completed);
Console.WriteLine();
Console.WriteLine("All expectations passed: refund_payment was never called before verify_identity.");

if (record)
{
    string path = Path.Combine(AppContext.BaseDirectory, "demo.trajectory");
    recorder!.ToTrajectory().Save(path);
    Console.WriteLine($"Recorded {recorder.ToTrajectory().Turns.Count} turn(s) to {path}");
}

return 0;

namespace RecordReplay
{
    internal static partial class SupportTools
    {
        /// <summary>Verifies a customer's identity.</summary>
        /// <param name="customerId">The customer id.</param>
        [ClaudeTool]
        public static string VerifyIdentity(string customerId) => $"identity of {customerId} verified";

        /// <summary>Refunds a payment to a verified customer.</summary>
        /// <param name="customerId">The customer id.</param>
        /// <param name="amount">The amount to refund.</param>
        [ClaudeTool]
        public static string RefundPayment(string customerId, double amount) =>
            $"refunded {amount} to {customerId}";
    }
}
