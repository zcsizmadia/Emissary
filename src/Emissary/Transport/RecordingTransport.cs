using System.Runtime.CompilerServices;

namespace Emissary.Transport;

/// <summary>Wraps a transport and records every completed exchange into a <see cref="TrajectoryRecorder"/>.</summary>
internal sealed class RecordingTransport : IModelTransport
{
    private readonly IModelTransport _inner;
    private readonly TrajectoryRecorder _recorder;

    public RecordingTransport(IModelTransport inner, TrajectoryRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        _inner = inner;
        _recorder = recorder;
    }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ModelResponse? response = null;
        await foreach (var streamEvent in _inner.StreamAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (streamEvent is StreamCompleted completed)
            {
                response = completed.Response;
            }

            yield return streamEvent;
        }

        if (response is not null)
        {
            _recorder.Add(new TrajectoryTurn(
                TrajectoryMapper.ToTrajectoryRequest(request),
                TrajectoryMapper.ToTrajectoryResponse(response)));
        }
    }
}
