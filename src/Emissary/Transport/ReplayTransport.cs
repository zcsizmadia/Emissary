using System.Runtime.CompilerServices;

namespace Emissary.Transport;

/// <summary>
/// Serves recorded trajectory turns instead of calling the API, verifying on each call that the
/// agent is making the same requests it made when the trajectory was recorded.
/// </summary>
internal sealed class ReplayTransport : IModelTransport
{
    private readonly Trajectory _trajectory;
    private int _index;

    public ReplayTransport(Trajectory trajectory)
    {
        ArgumentNullException.ThrowIfNull(trajectory);
        _trajectory = trajectory;
    }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_index >= _trajectory.Turns.Count)
        {
            throw new TrajectoryDivergenceException(
                $"The run made more model calls than the trajectory recorded ({_trajectory.Turns.Count}).");
        }

        var turn = _trajectory.Turns[_index++];
        Verify(request, turn.Request);

        var response = TrajectoryMapper.ToModelResponse(turn.Response);
        foreach (var streamEvent in TrajectoryMapper.SynthesizeEvents(response))
        {
            await Task.Yield();
            yield return streamEvent;
        }

        yield return new StreamCompleted(response);
    }

    private static void Verify(ModelRequest actual, TrajectoryRequest recorded)
    {
        if (actual.Model != recorded.Model)
        {
            throw new TrajectoryDivergenceException(
                $"Model diverged: recorded '{recorded.Model}', actual '{actual.Model}'.");
        }

        if (actual.Messages.Count != recorded.Messages.Count)
        {
            throw new TrajectoryDivergenceException(
                $"Conversation shape diverged: recorded {recorded.Messages.Count} message(s), actual {actual.Messages.Count}.");
        }

        if (!actual.Tools.Select(t => t.Name).SequenceEqual(recorded.ToolNames, StringComparer.Ordinal))
        {
            throw new TrajectoryDivergenceException(
                $"Tools diverged: recorded [{string.Join(", ", recorded.ToolNames)}], actual [{string.Join(", ", actual.Tools.Select(t => t.Name))}].");
        }
    }
}
