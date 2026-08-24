namespace Emissary;

/// <summary>Whether a replayed run executes its tools or serves their recorded results.</summary>
public enum ToolReplayMode
{
    /// <summary>
    /// Tools run for real while the model side is replayed. The model is deterministic, the tools
    /// are not: anything they touch is touched again, and anything they need must be reachable.
    /// </summary>
    Execute,

    /// <summary>
    /// Tool results come from the recording, so nothing outside the process is touched — a run
    /// whose tools query a database replays with no database. A call the recording does not cover
    /// is a <see cref="TrajectoryDivergenceException"/>, on the same principle as a diverging
    /// request: replay either reproduces the recorded run or says it cannot.
    /// </summary>
    FromRecording,
}

/// <summary>
/// The recorded results of a run's tool calls, keyed by tool-use id.
/// </summary>
/// <remarks>
/// Nothing extra is recorded to build this. A trajectory already contains every tool result,
/// because each recorded request carries the conversation sent — and that conversation includes the
/// tool results fed back from the previous turn. Replay serves the recorded assistant messages, so
/// the tool-use ids a replayed run produces are the recorded ones, which makes the id an exact key.
/// Existing <c>.trajectory</c> files therefore work as cassettes with no format change.
/// </remarks>
internal sealed class ToolCassette
{
    private readonly Dictionary<string, ToolResultBlock> _results;

    private ToolCassette(Dictionary<string, ToolResultBlock> results) => _results = results;

    /// <summary>Reads every tool result the trajectory contains.</summary>
    public static ToolCassette FromTrajectory(Trajectory trajectory)
    {
        var results = new Dictionary<string, ToolResultBlock>(StringComparer.Ordinal);
        foreach (var turn in trajectory.Turns)
        {
            foreach (var message in turn.Request.Messages)
            {
                foreach (var result in message.Content.OfType<ToolResultBlock>())
                {
                    // A later turn repeats earlier results verbatim, so first write wins.
                    results.TryAdd(result.ToolUseId, result);
                }
            }
        }

        return new ToolCassette(results);
    }

    /// <summary>The recorded result for a tool call.</summary>
    /// <param name="toolUse">The call the model asked for.</param>
    /// <exception cref="TrajectoryDivergenceException">The recording does not cover the call.</exception>
    public ToolResultBlock Replay(ToolUseBlock toolUse)
    {
        if (_results.TryGetValue(toolUse.Id, out var recorded))
        {
            return recorded;
        }

        throw new TrajectoryDivergenceException(
            $"Tool call diverged: '{toolUse.Name}' ({toolUse.Id}) has no recorded result, so it "
            + "cannot be replayed without executing it. Re-record the trajectory, or replay with "
            + $"{nameof(ToolReplayMode)}.{nameof(ToolReplayMode.Execute)}.");
    }
}
