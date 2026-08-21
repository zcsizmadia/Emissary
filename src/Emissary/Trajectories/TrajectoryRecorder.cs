namespace Emissary;

/// <summary>
/// Collects the turns of a live agent run. Pass one to the
/// <see cref="ClaudeAgent(AgentOptions, TrajectoryRecorder)"/> constructor, run the agent,
/// then call <see cref="ToTrajectory"/> to snapshot the recording.
/// </summary>
public sealed class TrajectoryRecorder
{
    private readonly Lock _lock = new();
    private readonly List<TrajectoryTurn> _turns = [];

    internal void Add(TrajectoryTurn turn)
    {
        lock (_lock)
        {
            _turns.Add(turn);
        }
    }

    /// <summary>Snapshots everything recorded so far as a <see cref="Trajectory"/>.</summary>
    public Trajectory ToTrajectory()
    {
        lock (_lock)
        {
            return new Trajectory(Trajectory.CurrentVersion, [.. _turns]);
        }
    }
}
