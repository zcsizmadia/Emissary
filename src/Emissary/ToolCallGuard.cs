namespace Emissary;

/// <summary>
/// Per-run enforcement of <see cref="ToolRules"/> and taint tracking. Checks are sequential
/// (in tool-use order) before a batch executes; results are recorded after.
/// </summary>
internal sealed class ToolCallGuard
{
    private readonly ToolRules _rules;
    private readonly HashSet<string> _succeeded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _attempts = new(StringComparer.Ordinal);
    private string? _terminatedBy;

    public ToolCallGuard(ToolRules rules)
    {
        _rules = rules;
    }

    public bool Tainted { get; private set; }

    public string? TaintSource { get; private set; }

    /// <summary>Captures the guard's state for durable suspension.</summary>
    public GuardSnapshot Snapshot() => new(
        [.. _succeeded],
        new Dictionary<string, int>(_attempts, StringComparer.Ordinal),
        _terminatedBy,
        Tainted,
        TaintSource);

    /// <summary>Rebuilds a guard from a snapshot taken by <see cref="Snapshot"/>.</summary>
    public static ToolCallGuard Restore(ToolRules rules, GuardSnapshot snapshot)
    {
        var guard = new ToolCallGuard(rules)
        {
            _terminatedBy = snapshot.TerminatedBy,
            Tainted = snapshot.Tainted,
            TaintSource = snapshot.TaintSource,
        };
        guard._succeeded.UnionWith(snapshot.Succeeded);
        foreach (var (tool, attempts) in snapshot.Attempts)
        {
            guard._attempts[tool] = attempts;
        }

        return guard;
    }

    /// <summary>Returns the violation message, or <see langword="null"/> when the call may run.</summary>
    public string? Check(ToolDefinition tool)
    {
        if (_terminatedBy is not null)
        {
            return $"Contract violation: no tool calls are allowed after terminal tool '{_terminatedBy}'.";
        }

        if (tool.Privileged && Tainted)
        {
            return $"Blocked: privileged tool '{tool.Name}' cannot run after untrusted content from tool '{TaintSource}' entered the conversation.";
        }

        if (_rules.Prerequisites.TryGetValue(tool.Name, out string? prerequisite)
            && !_succeeded.Contains(prerequisite))
        {
            return $"Contract violation: tool '{tool.Name}' requires a prior successful call to '{prerequisite}'.";
        }

        if (_rules.Limits.TryGetValue(tool.Name, out int limit))
        {
            int attempts = _attempts.GetValueOrDefault(tool.Name);
            if (attempts >= limit)
            {
                return $"Contract violation: tool '{tool.Name}' exceeded its limit of {limit} call(s).";
            }
        }

        _attempts[tool.Name] = _attempts.GetValueOrDefault(tool.Name) + 1;
        if (_rules.Terminals.Contains(tool.Name))
        {
            _terminatedBy = tool.Name;
        }

        return null;
    }

    /// <summary>Records an executed call's outcome, updating prerequisite and taint state.</summary>
    public void Record(ToolDefinition tool, bool success)
    {
        if (!success)
        {
            return;
        }

        _succeeded.Add(tool.Name);
        if (tool.Untrusted && !Tainted)
        {
            Tainted = true;
            TaintSource = tool.Name;
        }
    }
}
