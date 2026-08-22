using System.Net.Http;

namespace Emissary.Transport;

/// <summary>Pure retry-decision and backoff logic — the testable heart of <see cref="ResilientTransport"/>.</summary>
internal static class ResiliencePolicy
{
    /// <summary>
    /// The built-in transient-error classifier: network errors, timeouts, and Anthropic
    /// rate-limit / overloaded / 5xx exceptions (matched by type name to avoid a hard coupling
    /// to SDK exception types). User cancellation is never transient.
    /// </summary>
    public static bool IsTransient(Exception exception)
    {
        switch (exception)
        {
            case HttpRequestException:
            case TimeoutException:
                return true;
            case OperationCanceledException:
                return false;
            default:
                string name = exception.GetType().Name;
                return name.Contains("RateLimit", StringComparison.Ordinal)
                    || name.Contains("Overloaded", StringComparison.Ordinal)
                    || name.Contains("ServiceUnavailable", StringComparison.Ordinal)
                    || name.Contains("InternalServer", StringComparison.Ordinal)
                    || name.Contains("5xx", StringComparison.Ordinal);
        }
    }

    /// <summary>The backoff delay before the given zero-based retry attempt.</summary>
    public static TimeSpan NextDelay(int attempt, ResilienceOptions options)
    {
        double scaled = options.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt);
        double capped = Math.Min(scaled, options.MaxDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(capped);
    }
}
