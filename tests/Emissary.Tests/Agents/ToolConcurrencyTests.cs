using Emissary.Tests.Agents;

namespace Emissary.Tests;

/// <summary>
/// A turn can ask for many tool calls at once. Without a cap they all run concurrently, which is
/// fine for cheap tools and not fine for a database pool.
/// </summary>
public sealed class ToolConcurrencyTests
{
    /// <summary>Records how many calls were in flight at the same time.</summary>
    private sealed class ConcurrencyProbe
    {
        private int _inFlight;

        public int Peak { get; private set; }

        public async Task<string> RunAsync()
        {
            int current = Interlocked.Increment(ref _inFlight);
            lock (this)
            {
                Peak = Math.Max(Peak, current);
            }

            // Long enough that calls genuinely overlap when they are allowed to.
            await Task.Delay(25);
            Interlocked.Decrement(ref _inFlight);
            return "ok";
        }
    }

    private static async Task<(AgentResult Result, int Peak, FakeTransport Transport)> RunSixCallsAsync(
        int? maxParallel)
    {
        var probe = new ConcurrencyProbe();
        var options = new AgentOptions { MaxParallelTools = maxParallel };
        options.Tools.Add(new ToolDefinition(
            "work", "Does slow work.", """{"type":"object","properties":{}}""",
            async (_, _) => await probe.RunAsync()));

        var transport = new FakeTransport();
        transport.EnqueueTurn(FakeTransport.ToolTurn(
            [.. Enumerable.Range(0, 6).Select(i => FakeTransport.Use($"t{i}", "work", "{}"))]));
        transport.EnqueueTurn(FakeTransport.TextTurn("all done"));

        var result = await new ClaudeAgent(options, transport).RunAsync("do six things");
        return (result, probe.Peak, transport);
    }

    [Test]
    public async Task Without_a_cap_the_calls_of_a_turn_overlap()
    {
        var (result, peak, _) = await RunSixCallsAsync(maxParallel: null);

        await Assert.That(peak).IsGreaterThan(1);
        await Assert.That(result.StopReason).IsEqualTo(AgentStopReason.Completed);
    }

    [Test]
    public async Task A_cap_bounds_how_many_run_at_once()
    {
        var (result, peak, _) = await RunSixCallsAsync(maxParallel: 2);

        await Assert.That(peak).IsLessThanOrEqualTo(2);
        await Assert.That(result.FinalText).IsEqualTo("all done");
    }

    [Test]
    public async Task A_cap_of_one_serializes_the_calls()
    {
        var (_, peak, _) = await RunSixCallsAsync(maxParallel: 1);

        await Assert.That(peak).IsEqualTo(1);
    }

    [Test]
    public async Task Every_call_still_runs_and_results_keep_their_order()
    {
        var options = new AgentOptions { MaxParallelTools = 2 };
        options.Tools.Add(new ToolDefinition(
            "echo_slow", "Echoes after a pause.",
            """{"type":"object","properties":{"text":{"type":"string"}}}""",
            async (input, token) =>
            {
                await Task.Delay(10, token);
                return input.GetProperty("text").GetString()!;
            }));

        var transport = new FakeTransport();
        transport.EnqueueTurn(FakeTransport.ToolTurn(
            FakeTransport.Use("t0", "echo_slow", """{"text":"first"}"""),
            FakeTransport.Use("t1", "echo_slow", """{"text":"second"}"""),
            FakeTransport.Use("t2", "echo_slow", """{"text":"third"}""")));
        transport.EnqueueTurn(FakeTransport.TextTurn("done"));

        await new ClaudeAgent(options, transport).RunAsync("go");

        // Results are fed back in tool_use order regardless of which finished first.
        var results = transport.Requests[1].Messages[^1].Content.Cast<ToolResultBlock>().ToList();
        await Assert.That(results.Select(r => r.ToolUseId)).IsEquivalentTo(["t0", "t1", "t2"]);
        await Assert.That(results.Select(r => r.Content)).IsEquivalentTo(["first", "second", "third"]);
    }

    [Test]
    public async Task A_capped_run_can_still_be_cancelled_while_calls_are_queued()
    {
        var started = new TaskCompletionSource();
        var options = new AgentOptions { MaxParallelTools = 1 };
        options.Tools.Add(new ToolDefinition(
            "block", "Blocks.", """{"type":"object","properties":{}}""",
            async (_, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return "unreachable";
            }));

        var transport = new FakeTransport();
        transport.EnqueueTurn(FakeTransport.ToolTurn(
            FakeTransport.Use("t0", "block", "{}"),
            FakeTransport.Use("t1", "block", "{}")));
        transport.EnqueueTurn(FakeTransport.TextTurn("unreachable"));

        using var cancellation = new CancellationTokenSource();
        var run = new ClaudeAgent(options, transport).RunAsync("go", cancellation.Token);
        await started.Task;
        await cancellation.CancelAsync();

        // The second call was still waiting on the semaphore; cancelling must not hang.
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await run);
    }
}
