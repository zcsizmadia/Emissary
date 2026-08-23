using System.Globalization;

namespace Emissary.Testing;

/// <summary>
/// Chainable expectations about an agent run's behavior: which tools were called, in what
/// order, how the run stopped, and what it answered. Failures throw
/// <see cref="EmissaryAssertionException"/> with the expectation and the observed behavior.
/// </summary>
public sealed class AgentRunExpectations
{
    private readonly AgentResult _result;
    private readonly List<ToolUseBlock> _toolUses;

    internal AgentRunExpectations(AgentResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _result = result;
        _toolUses = result.Conversation.Messages
            .Where(m => m.Role == MessageRole.Assistant)
            .SelectMany(m => m.Content.OfType<ToolUseBlock>())
            .ToList();
    }

    /// <summary>The run must have called the tool at least once.</summary>
    /// <param name="name">The wire name of the tool.</param>
    public AgentRunExpectations ToolCalled(string name)
    {
        if (!_toolUses.Any(t => t.Name == name))
        {
            throw Failure($"expected tool '{name}' to be called, but it never was. Called: {CalledTools()}.");
        }

        return this;
    }

    /// <summary>The run must have called the tool exactly this many times.</summary>
    /// <param name="name">The wire name of the tool.</param>
    /// <param name="times">The exact expected call count.</param>
    public AgentRunExpectations ToolCalled(string name, int times)
    {
        int actual = _toolUses.Count(t => t.Name == name);
        if (actual != times)
        {
            throw Failure($"expected tool '{name}' to be called {times} time(s), but it was called {actual} time(s).");
        }

        return this;
    }

    /// <summary>The run must never have called the tool.</summary>
    /// <param name="name">The wire name of the tool.</param>
    public AgentRunExpectations ToolNotCalled(string name)
    {
        if (_toolUses.Any(t => t.Name == name))
        {
            throw Failure($"expected tool '{name}' to never be called, but it was. Called: {CalledTools()}.");
        }

        return this;
    }

    /// <summary>
    /// The run must never have called <paramref name="name"/> before the first call to
    /// <paramref name="requiredPredecessor"/> — e.g. never <c>refund_payment</c> before
    /// <c>verify_identity</c>. Passes if <paramref name="name"/> was never called at all.
    /// </summary>
    /// <param name="name">The guarded tool.</param>
    /// <param name="requiredPredecessor">The tool that must come first.</param>
    public AgentRunExpectations ToolNotCalledBefore(string name, string requiredPredecessor)
    {
        int firstGuarded = _toolUses.FindIndex(t => t.Name == name);
        if (firstGuarded < 0)
        {
            return this;
        }

        int firstPredecessor = _toolUses.FindIndex(t => t.Name == requiredPredecessor);
        if (firstPredecessor < 0 || firstGuarded < firstPredecessor)
        {
            throw Failure($"expected tool '{name}' to only be called after '{requiredPredecessor}', but it was called {(firstPredecessor < 0 ? "without" : "before")} it. Order: {CalledTools()}.");
        }

        return this;
    }

    /// <summary>The run must have stopped for this reason.</summary>
    /// <param name="expected">The expected stop reason.</param>
    public AgentRunExpectations Stopped(AgentStopReason expected)
    {
        if (_result.StopReason != expected)
        {
            throw Failure($"expected the run to stop with {expected}, but it stopped with {_result.StopReason}.");
        }

        return this;
    }

    /// <summary>Untrusted tool output must have entered the conversation during the run.</summary>
    public AgentRunExpectations Tainted()
    {
        if (!_result.Tainted)
        {
            throw Failure("expected the run to be tainted by untrusted tool output, but it was not.");
        }

        return this;
    }

    /// <summary>No untrusted tool output may have entered the conversation during the run.</summary>
    public AgentRunExpectations NotTainted()
    {
        if (_result.Tainted)
        {
            throw Failure("expected the run to be untainted, but untrusted tool output entered the conversation.");
        }

        return this;
    }

    /// <summary>A shadow run must have planned (intercepted) a call to this tool.</summary>
    /// <param name="toolName">The wire name of the privileged tool.</param>
    public AgentRunExpectations EffectPlanned(string toolName)
    {
        if (!_result.PlannedEffects.Any(e => e.ToolName == toolName))
        {
            throw Failure($"expected a planned effect for tool '{toolName}', but the plan contains: {DescribePlan()}.");
        }

        return this;
    }

    /// <summary>The run must not have planned any effects (live run, or shadow run with none).</summary>
    public AgentRunExpectations NoPlannedEffects()
    {
        if (_result.PlannedEffects.Count > 0)
        {
            throw Failure($"expected no planned effects, but the plan contains: {DescribePlan()}.");
        }

        return this;
    }

    private string DescribePlan() =>
        _result.PlannedEffects.Count == 0
            ? "(none)"
            : string.Join(", ", _result.PlannedEffects.Select(e => e.ToolName));

    /// <summary>
    /// A tool must have failed during the run — its handler threw and the failure was reported to
    /// the model rather than ending the run.
    /// </summary>
    /// <param name="toolName">The wire name of the tool expected to have failed.</param>
    public AgentRunExpectations ToolFailed(string toolName)
    {
        if (!_result.ToolFailures.Any(f => f.ToolName == toolName))
        {
            throw Failure($"expected tool '{toolName}' to have failed, but the failures were: {DescribeFailures()}.");
        }

        return this;
    }

    /// <summary>
    /// A tool must have been cancelled for exceeding
    /// <see cref="ToolFailureOptions.Timeout"/> — distinct from a tool that threw.
    /// </summary>
    /// <param name="toolName">The wire name of the tool expected to have timed out.</param>
    public AgentRunExpectations ToolTimedOut(string toolName)
    {
        if (!_result.ToolFailures.Any(f => f.ToolName == toolName && f.TimedOut))
        {
            throw Failure($"expected tool '{toolName}' to have timed out, but the failures were: {DescribeFailures()}.");
        }

        return this;
    }

    /// <summary>
    /// No tool may have failed. Worth asserting on a golden run: a tool that starts throwing is
    /// otherwise invisible, because the model narrates its way around the failure.
    /// </summary>
    public AgentRunExpectations NoToolFailures()
    {
        if (_result.ToolFailures.Count > 0)
        {
            throw Failure($"expected no tool failures, but got: {DescribeFailures()}.");
        }

        return this;
    }

    /// <summary>
    /// The run must have produced a complete answer: <see cref="AgentStopReason.Completed"/>. Fails
    /// on every reason that leaves the answer cut short — a truncated response, a refusal, a paused
    /// turn, an exhausted budget or turn limit, or a run waiting for approval — and names it.
    /// </summary>
    public AgentRunExpectations Complete()
    {
        if (_result.StopReason != AgentStopReason.Completed)
        {
            throw Failure(
                $"expected a complete answer, but the run stopped with {_result.StopReason}, "
                + "so the final text is not the whole answer.");
        }

        return this;
    }

    private string DescribeFailures() =>
        _result.ToolFailures.Count == 0
            ? "(none)"
            : string.Join(", ", _result.ToolFailures.Select(
                f => $"{f.ToolName}: {f.Exception.GetType().Name}{(f.TimedOut ? " (timed out)" : "")}"));

    /// <summary>The final assistant text must contain this fragment (ordinal comparison).</summary>
    /// <param name="expected">The expected fragment.</param>
    public AgentRunExpectations FinalTextContains(string expected)
    {
        if (!_result.FinalText.Contains(expected, StringComparison.Ordinal))
        {
            throw Failure($"expected the final text to contain \"{expected}\", but it was \"{_result.FinalText}\".");
        }

        return this;
    }

    private string CalledTools() =>
        _toolUses.Count == 0 ? "(none)" : string.Join(" -> ", _toolUses.Select(t => t.Name));

    private static EmissaryAssertionException Failure(string message) =>
        new(string.Create(CultureInfo.InvariantCulture, $"Agent run assertion failed: {message}"));
}
