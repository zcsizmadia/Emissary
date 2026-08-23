namespace Emissary;

/// <summary>What happens when a tool handler throws.</summary>
public enum ToolFailureMode
{
    /// <summary>
    /// The failure is reported to the model as an error tool result, so it can retry, try another
    /// tool, or tell the user — the run continues. Recommended: a tool failing (a timeout, a 404,
    /// a locked row) is an operational event, not a reason to lose the whole conversation.
    /// </summary>
    ReportToModel,

    /// <summary>
    /// The exception propagates out of the run. Choose this when a failing tool means the run's
    /// result cannot be trusted, or in tests where an unexpected throw should fail loudly.
    /// </summary>
    Propagate,
}

/// <summary>How tool failures and slow tools are handled.</summary>
public sealed class ToolFailureOptions
{
    /// <summary>What happens when a tool handler throws. Defaults to
    /// <see cref="ToolFailureMode.ReportToModel"/>.</summary>
    public ToolFailureMode Mode { get; set; } = ToolFailureMode.ReportToModel;

    /// <summary>
    /// Whether the exception's message is included in what the model is told.
    /// <see langword="false"/> by default, because exception messages carry connection strings,
    /// file paths, SQL, and record data — and everything the model sees is sent to the API and
    /// echoed into its reply. With it off, the model is told the tool failed and the exception
    /// type; the exception itself reaches your code through
    /// <see cref="AgentToolFailedEvent"/> and <see cref="AgentResult.ToolFailures"/>, and the
    /// activity for the call records it.
    /// </summary>
    public bool IncludeExceptionMessage { get; set; }

    /// <summary>
    /// How long a single tool call may run before it is cancelled and reported as a failure.
    /// <see langword="null"/> (the default) means no limit, so a handler that never returns
    /// stalls the run.
    /// </summary>
    public TimeSpan? Timeout { get; set; }
}

/// <summary>A tool handler that threw during a run.</summary>
/// <param name="ToolUseId">The tool-use id of the failed call.</param>
/// <param name="ToolName">The wire name of the tool.</param>
/// <param name="Exception">The exception the handler threw, in full.</param>
/// <param name="TimedOut">Whether the call was cancelled for exceeding
/// <see cref="ToolFailureOptions.Timeout"/>.</param>
public sealed record ToolFailure(string ToolUseId, string ToolName, Exception Exception, bool TimedOut);

/// <summary>
/// A tool handler threw and the failure was reported to the model
/// (<see cref="ToolFailureMode.ReportToModel"/>). Carries the exception in full, whatever the model
/// was told — see <see cref="ToolFailureOptions.IncludeExceptionMessage"/>.
/// </summary>
/// <param name="Failure">The failed call.</param>
public sealed record AgentToolFailedEvent(ToolFailure Failure) : AgentEvent;
