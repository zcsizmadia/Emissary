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
        var (enumerator, hasCurrent) = await EstablishAsync(request, cancellationToken).ConfigureAwait(false);
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
        }
    }

    private async Task<(IAsyncEnumerator<StreamEvent> Enumerator, bool HasCurrent)> EstablishAsync(
        ModelRequest request,
        CancellationToken cancellationToken)
    {
        Func<Exception, bool> shouldRetry = _options.ShouldRetry ?? ResiliencePolicy.IsTransient;

        for (int attempt = 0; ; attempt++)
        {
            using var attemptCts = CreateAttemptCts(cancellationToken, out CancellationToken attemptToken);
            var enumerator = _inner.StreamAsync(request, attemptToken).GetAsyncEnumerator(attemptToken);
            try
            {
                bool hasCurrent = await enumerator.MoveNextAsync().ConfigureAwait(false);
                return (enumerator, hasCurrent);
            }
            catch (Exception exception)
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);

                // Never retry genuine caller cancellation.
                cancellationToken.ThrowIfCancellationRequested();

                bool timedOut = exception is OperationCanceledException && attemptToken.IsCancellationRequested;
                bool retryable = timedOut || shouldRetry(exception);
                if (attempt >= _options.MaxRetries || !retryable)
                {
                    throw;
                }

                await Task.Delay(ResiliencePolicy.NextDelay(attempt, _options), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private CancellationTokenSource CreateAttemptCts(CancellationToken cancellationToken, out CancellationToken attemptToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_options.RequestTimeout is { } timeout)
        {
            cts.CancelAfter(timeout);
        }

        attemptToken = cts.Token;
        return cts;
    }
}
