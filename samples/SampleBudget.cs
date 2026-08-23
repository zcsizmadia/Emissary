using Emissary;

/// <summary>
/// Cost limits shared by every sample, linked into each sample project so the values live in one
/// place (the same reason the model id does).
/// </summary>
/// <remarks>
/// <para>
/// A sample exists to show <i>mechanics</i> — a tool loop, a contract, a handoff — none of which
/// need a frontier model. Emissary's own default is
/// <see cref="EmissaryDefaults.Model"/> because that is the right default for an agent doing real
/// work, but running the samples on it means every <c>dotnet run</c> spends real money at the most
/// expensive tier, with <see cref="AgentOptions.TokenBudget"/> unset and up to
/// <see cref="AgentOptions.MaxTurns"/> model calls behind one command.
/// </para>
/// <para>
/// So the samples pin a small model, a hard token budget, and a low turn limit. A sample run should
/// cost a fraction of a cent, and a sample that misbehaves should stop rather than spend.
/// </para>
/// </remarks>
internal static class SampleBudget
{
    /// <summary>The cheapest current model — enough for every mechanic the samples demonstrate.</summary>
    public const string Model = "claude-haiku-4-5-20251001";

    /// <summary>
    /// Hard ceiling on input + output tokens for one run. The run stops with
    /// <see cref="AgentStopReason.BudgetExceeded"/> rather than continuing to spend.
    /// </summary>
    public const long TokenBudget = 50_000;

    /// <summary>
    /// Model calls per run. Emissary's default of 16 is a loop guard for production agents; a
    /// sample that needs more than a handful of turns is stuck, and should stop cheaply.
    /// </summary>
    public const int MaxTurns = 6;

    /// <summary>Applies all three limits to options a sample is about to run.</summary>
    /// <param name="options">The options to constrain.</param>
    public static AgentOptions Constrain(AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Model = Model;
        options.TokenBudget = TokenBudget;
        options.MaxTurns = MaxTurns;
        return options;
    }
}
