using System.Net.Http;
using Anthropic.Exceptions;

namespace Emissary.Transport;

/// <summary>Pure retry-decision and backoff logic — the testable heart of <see cref="ResilientTransport"/>.</summary>
internal static class ResiliencePolicy
{
    /// <summary>
    /// The built-in transient-error classifier, written against the SDK's actual exception types.
    /// It previously matched on type <i>names</i> to avoid coupling to the SDK, and got the answer
    /// wrong: <see cref="AnthropicIOException"/> — every connection refusal, reset, DNS and TLS
    /// failure — matched nothing and was never retried, while the tests asserted against invented
    /// exception classes the SDK never throws (see ADR 0008). Coupling to types the compiler can
    /// check is worth more here than nominal independence.
    /// </summary>
    public static bool IsTransient(Exception exception) => exception switch
    {
        // Cancellation is the caller's decision. A per-attempt timeout is recognized by the
        // transport, which knows whether it was the one that cancelled.
        OperationCanceledException => false,

        // The request never reached the API: connection refused or reset, DNS, TLS.
        AnthropicIOException => true,

        // The API answered — retry only the statuses that mean "ask again later".
        AnthropicApiException api => IsTransientStatus((int)api.StatusCode),

        HttpRequestException or TimeoutException => true,
        _ => false,
    };

    // 408 request timeout, 429 rate limited, and everything 5xx — which includes Anthropic's 529
    // overloaded_error.
    private static bool IsTransientStatus(int statusCode) =>
        statusCode is 408 or 429 || statusCode >= 500;

    /// <summary>The backoff delay before the given zero-based retry attempt.</summary>
    public static TimeSpan NextDelay(int attempt, ResilienceOptions options)
    {
        double scaled = options.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt);
        double capped = Math.Min(scaled, options.MaxDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(capped);
    }
}
