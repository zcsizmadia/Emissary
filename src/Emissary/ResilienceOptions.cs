namespace Emissary;

/// <summary>
/// Controls how transient failures talking to the Claude API are handled: retries with
/// exponential backoff, and an optional per-attempt timeout. Retries happen only before the
/// first streamed event of a turn — once output has started, the stream is never re-issued.
/// </summary>
public sealed class ResilienceOptions
{
    /// <summary>Maximum retry attempts after the initial try (0 disables retries). Default 2.</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>Base backoff delay; attempt <c>n</c> waits <c>BaseDelay * 2^n</c>, capped at <see cref="MaxDelay"/>. Default 500ms.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Upper bound on a single backoff delay. Default 30s.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long one attempt may take to <b>start streaming</b> — the time to the first event.
    /// A timed-out attempt is retried. Once the stream is established the answer may take as long
    /// as it takes, since a long answer is not a failure; from then on only the caller's
    /// cancellation token stops it. Also caps the underlying SDK client's own request timeout, whose
    /// default is 10 minutes. <see langword="null"/> means no timeout of our own.
    /// </summary>
    public TimeSpan? RequestTimeout { get; set; }

    /// <summary>
    /// Overrides which exceptions are treated as transient (retryable). When <see langword="null"/>,
    /// the built-in classifier retries connection failures and the API statuses that mean "ask again
    /// later" (408, 429, and 5xx including Anthropic's 529 overloaded).
    /// </summary>
    public Func<Exception, bool>? ShouldRetry { get; set; }
}
