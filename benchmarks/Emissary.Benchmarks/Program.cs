using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Emissary;
using Emissary.Benchmarks;

BenchmarkSwitcher.FromTypes([typeof(ToolDispatchBenchmarks), typeof(TrajectoryBenchmarks)]).Run(args);
return 0;

namespace Emissary.Benchmarks
{
    /// <summary>The cost of Emissary's generated, reflection-free tool machinery.</summary>
    [MemoryDiagnoser]
    public partial class ToolDispatchBenchmarks
    {
        private static readonly JsonDocument Input =
            JsonDocument.Parse("""{"order_id":"A-1001","amount":39.99}""");

        [ClaudeTool(Description = "Refunds a payment for an order.")]
        public static string RefundPayment(string orderId, double amount) => $"refunded {amount} for {orderId}";

        /// <summary>Bind JSON arguments, invoke the method, convert the result — the full dispatch path.</summary>
        [Benchmark]
        public async ValueTask<string> DispatchTool() =>
            await RefundPaymentTool.Handler(Input.RootElement, CancellationToken.None);

        /// <summary>Schema access is a constant lookup — the schema was built at compile time.</summary>
        [Benchmark]
        public string SchemaAccess() => RefundPaymentTool.InputSchemaJson;
    }

    /// <summary>The cost of the record/replay machinery that makes agent runs deterministic.</summary>
    [MemoryDiagnoser]
    public partial class TrajectoryBenchmarks
    {
        private Trajectory _trajectory = null!;
        private string _json = null!;
        private AgentOptions _options = null!;

        [ClaudeTool(Description = "Echoes text.")]
        public static string Echo(string text) => text;

        [GlobalSetup]
        public async Task Setup()
        {
            _options = new AgentOptions { Tools = { EchoTool } };
            using var document = JsonDocument.Parse("""{"text":"ping"}""");

            // Record a two-turn tool loop once; every benchmark replays it.
            var turns = new List<TrajectoryTurn>
            {
                new(
                    new TrajectoryRequest("claude-opus-5", null, 4096, ThinkingMode.Adaptive, null, null,
                        [Message.User("go")], ["echo"]),
                    new TrajectoryResponse(
                        [new ToolUseBlock("t1", "echo", document.RootElement.Clone())], "tool_use", 10, 5)),
                new(
                    new TrajectoryRequest("claude-opus-5", null, 4096, ThinkingMode.Adaptive, null, null,
                        [Message.User("go"),
                         new Message(MessageRole.Assistant, [new ToolUseBlock("t1", "echo", document.RootElement.Clone())]),
                         new Message(MessageRole.User, [new ToolResultBlock("t1", "ping", false)])],
                        ["echo"]),
                    new TrajectoryResponse([new TextBlock("done")], "end_turn", 20, 9)),
            };
            _trajectory = new Trajectory(Trajectory.CurrentVersion, turns);
            _json = _trajectory.ToJson();

            // Warm validation: the replayed run must complete.
            _ = await new ClaudeAgent(_options, _trajectory).RunAsync("go");
        }

        /// <summary>A complete two-turn agent run (tool call + final answer), zero network.</summary>
        [Benchmark]
        public async Task<string> ReplayToolLoopRun()
        {
            var agent = new ClaudeAgent(_options, _trajectory);
            var result = await agent.RunAsync("go");
            return result.FinalText;
        }

        /// <summary>Trajectory round trip through JSON.</summary>
        [Benchmark]
        public Trajectory SerializeRoundTrip() => Trajectory.FromJson(_trajectory.ToJson());

        /// <summary>Trajectory parse alone.</summary>
        [Benchmark]
        public Trajectory Deserialize() => Trajectory.FromJson(_json);
    }
}
