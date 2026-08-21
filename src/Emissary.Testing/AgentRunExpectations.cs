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
