using Emissary.Testing;
using Emissary.Tests.Tools;

namespace Emissary.Tests;

/// <summary>
/// The golden suite: checked-in .trajectory files replayed deterministically in CI — zero
/// network, byte-identical behavior on every run. This is also the dogfood test for
/// Emissary.Testing's assertion API.
/// </summary>
public sealed class GoldenTrajectoryTests
{
    private static Trajectory LoadGolden(string name) =>
        Trajectory.Load(Path.Combine(AppContext.BaseDirectory, "Trajectories", name));

    [Test]
    public async Task Tool_loop_golden_trajectory_replays_deterministically()
    {
        var trajectory = LoadGolden("tool-loop.trajectory");
        var agent = new ClaudeAgent(
            new AgentOptions { SystemPrompt = "Use tools.", Tools = { SampleTools.EchoTool } },
            trajectory);

        var result = await agent.RunAsync("go");

        EmissaryAssert.That(result)
            .ToolCalled("echo", times: 1)
            .ToolNotCalled("add")
            .Stopped(AgentStopReason.Completed)
            .FinalTextContains("done");
        await Assert.That(result.FinalText).IsEqualTo("done");
        await Assert.That(result.Usage).IsEqualTo(new AgentUsage(30, 14));
    }
}
