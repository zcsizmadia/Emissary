using System.Runtime.CompilerServices;

namespace Emissary.Transport;

/// <summary>
/// Wraps a transport with retries and exponential backoff for transient failures, plus an
/// optional per-attempt timeout. Retries only re-issue the request while establishing the
/// stream (before the first event); a failure mid-stream propagates, since streamed output
/// cannot be safely replayed.
/// </summary>
internal sealed class ResilientTransport : IModelTransport
{
    private readonly IModelTransport _inner;
    private readonly ResilienceOptions _options;

    public ResilientTransport(IModelTransport inner, ResilienceOptions options)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxRetries, 0);
        _inner = inner;
        _options = options;
    }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var established = await EstablishAsync(request, cancellationToken).ConfigureAwait(false);
        var enumerator = established.Enumerator;
        bool hasCurrent = established.HasCurrent;
        try
        {
            while (hasCurrent)
            {
                yield return enumerator.Current;
                hasCurrent = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);

            // The linked source must outlive the enumerator it produced the token for: disposing it
            // earlier unlinks it from the caller's token, silently, so cancelling the run would stop
            // nothing while output kept streaming and billing.
            established.Timeout?.Dispose();
        }
    }

    private async Task<(IAsyncEnumerator<StreamEvent> Enumerator, bool HasCurrent, CancellationTokenSource? Timeout)>
        EstablishAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        Func<Exception, bool> shouldRetry = _options.ShouldRetry ?? ResiliencePolicy.IsTransient;

        for (int attempt = 0; ; attempt++)
        {
            var attemptCts = CreateAttemptCts(cancellationToken, out CancellationToken attemptToken);
            var enumerator = _inner.StreamAsync(request, attemptToken).GetAsyncEnumerator(attemptToken);
            try
            {
                bool hasCurrent = await enumerator.MoveNextAsync().ConfigureAwait(false);

                // Established, so the timeout has done its job. Stop its timer but keep the source
                // alive, so the caller's token still reaches the stream.
                attemptCts?.CancelAfter(System.Threading.Timeout.InfiniteTimeSpan);
                return (enumerator, hasCurrent, attemptCts);
            }
            catch (Exception exception)
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
                attemptCts?.Dispose();

                // Never retry genuine caller cancellation.
                cancellationToken.ThrowIfCancellationRequested();

                bool timedOut = exception is OperationCanceledException && _options.RequestTimeout is not null;
                bool retryable = timedOut || shouldRetry(exception);
                if (attempt >= _options.MaxRetries || !retryable)
                {
                    throw;
                }

                await Task.Delay(ResiliencePolicy.NextDelay(attempt, _options), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // Only needed when a timeout is configured; otherwise the caller's token is passed straight
    // through, with nothing extra to keep alive or dispose.
    private CancellationTokenSource? CreateAttemptCts(
        CancellationToken cancellationToken,
        out CancellationToken attemptToken)
    {
        if (_options.RequestTimeout is not { } timeout)
        {
            attemptToken = cancellationToken;
            return null;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        attemptToken = cts.Token;
        return cts;
    }
}
