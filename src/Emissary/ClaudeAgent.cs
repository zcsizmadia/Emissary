using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Emissary.Transport;

namespace Emissary;

/// <summary>
/// The agent loop: sends the conversation to Claude, streams the response, executes requested
/// tools (in parallel), feeds results back, and repeats until the model finishes or a limit hits.
/// </summary>
public sealed class ClaudeAgent
{
    private readonly AgentOptions _options;
    private readonly IModelTransport _transport;
    private readonly ToolDefinition[] _activeTools;

    /// <summary>Creates an agent talking to the Claude API.</summary>
    /// <param name="options">The agent configuration.</param>
    public ClaudeAgent(AgentOptions options)
        : this(options, BuildLiveTransport(options))
    {
    }

    /// <summary>Creates an agent talking to the Claude API, recording every exchange.</summary>
    /// <param name="options">The agent configuration.</param>
    /// <param name="recorder">Receives every completed model exchange.</param>
    public ClaudeAgent(AgentOptions options, TrajectoryRecorder recorder)
        : this(options, new RecordingTransport(BuildLiveTransport(options), recorder))
    {
    }

    // The live transport, wrapped with retry/backoff (ADR: resilience is transport-level so it
    // sits under recording — a retried attempt is recorded once — and off the deterministic replay path).
    private static ResilientTransport BuildLiveTransport(AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new ResilientTransport(new AnthropicTransport(options.ApiKey), options.Resilience);
    }

    /// <summary>Creates an agent that replays a recorded trajectory instead of calling the API.</summary>
    /// <param name="options">The agent configuration; must match the recorded run.</param>
    /// <param name="trajectory">The recording to replay.</param>
    /// <exception cref="TrajectoryDivergenceException">Thrown during a run if it diverges from the recording.</exception>
    public ClaudeAgent(AgentOptions options, Trajectory trajectory)
        : this(options, new ReplayTransport(trajectory))
    {
    }

    internal ClaudeAgent(AgentOptions options, IModelTransport transport)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxTurns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxTokens, 1);
        if (options.TokenBudget is { } tokenBudget)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(tokenBudget, 1, nameof(options));
        }
        if (options.Tools.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count() != options.Tools.Count)
        {
            throw new ArgumentException("Tool names must be unique.", nameof(options));
        }

        _options = options;
        _transport = transport;

        // Pre-prompt schema filtering: policy-gated tools the authorizer does not grant are
        // invisible to the model and unexecutable. No authorizer means deny by default.
        _activeTools = options.Tools
            .Where(t => t.RequiredPolicy is null || options.Authorizer?.IsAuthorized(t) == true)
            .ToArray();
    }

    /// <summary>Runs the agent on a single user message and returns the outcome.</summary>
    /// <param name="userInput">The user's text.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    public Task<AgentResult> RunAsync(string userInput, CancellationToken cancellationToken = default) =>
        RunAsync(Conversation.Start().Append(Message.User(userInput)), cancellationToken);

    /// <summary>
    /// Runs the agent and deserializes the final answer as <typeparamref name="T"/> — pair with
    /// <see cref="AgentOptions.WithOutput{T}"/> so the answer is schema-guaranteed.
    /// </summary>
    /// <typeparam name="T">The structured output type.</typeparam>
    /// <param name="userInput">The user's text.</param>
    /// <param name="typeInfo">Source-generated serializer metadata for <typeparamref name="T"/>.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    public async Task<T> RunAsync<T>(
        string userInput,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(userInput, cancellationToken).ConfigureAwait(false);
        return result.FinalAs(typeInfo);
    }

    /// <summary>
    /// Exposes this whole agent as a single tool for another agent — the composition primitive
    /// for sub-agent hierarchies. The tool takes a <c>message</c> and returns the sub-agent's
    /// final answer. Safety composes conservatively: the tool is marked
    /// <see cref="ToolDefinition.Untrusted"/> if this agent can read untrusted content, and
    /// <see cref="ToolDefinition.Privileged"/> if it can perform privileged effects — so a
    /// parent's taint tracking and contracts see through the boundary.
    /// </summary>
    /// <param name="name">The wire name of the composed tool.</param>
    /// <param name="description">The description shown to the parent's model.</param>
    public ToolDefinition AsTool(string name, string description)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(description);

        return new ToolDefinition(
            name,
            description,
            """{"type":"object","properties":{"message":{"type":"string"}},"required":["message"]}""",
            async (input, cancellationToken) =>
            {
                if (!input.TryGetProperty("message", out var messageProperty)
                    || messageProperty.GetString() is not { Length: > 0 } message)
                {
                    throw new ToolArgumentException($"Tool '{name}' is missing required argument 'message'.");
                }

                var result = await RunAsync(message, cancellationToken).ConfigureAwait(false);
                if (result.StopReason != AgentStopReason.Completed)
                {
                    throw new ToolArgumentException(
                        $"Sub-agent '{name}' stopped with {result.StopReason} before completing.");
                }

                return result.FinalText;
            },
            untrusted: _activeTools.Any(t => t.Untrusted),
            privileged: _activeTools.Any(t => t.Privileged));
    }

    /// <summary>Runs the agent on an existing conversation and returns the outcome.</summary>
    /// <param name="conversation">The conversation so far; the last message must be from the user.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    public async Task<AgentResult> RunAsync(Conversation conversation, CancellationToken cancellationToken = default)
    {
        AgentResult? result = null;
        await foreach (var agentEvent in StreamAsync(conversation, cancellationToken).ConfigureAwait(false))
        {
            if (agentEvent is AgentCompletedEvent completed)
            {
                result = completed.Result;
            }
        }

        return result!;
    }

    /// <summary>Runs the agent on a single user message, streaming events as they happen.</summary>
    /// <param name="userInput">The user's text.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    public IAsyncEnumerable<AgentEvent> StreamAsync(string userInput, CancellationToken cancellationToken = default) =>
        StreamAsync(Conversation.Start().Append(Message.User(userInput)), cancellationToken);

    /// <summary>Runs the agent on an existing conversation, streaming events as they happen.</summary>
    /// <param name="conversation">The conversation so far; the last message must be from the user.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    public IAsyncEnumerable<AgentEvent> StreamAsync(
        Conversation conversation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        return RunLoopAsync(conversation, AgentUsage.Zero, new ToolCallGuard(_options.Rules), [], cancellationToken);
    }

    /// <summary>Resumes a suspended run with a human decision and returns the outcome.</summary>
    /// <param name="run">The suspension state (from <see cref="AgentResult.Suspension"/> or a store).</param>
    /// <param name="approve">Whether the gated calls may execute; denial informs the model instead.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    public async Task<AgentResult> ResumeAsync(SuspendedRun run, bool approve, CancellationToken cancellationToken = default)
    {
        AgentResult? result = null;
        await foreach (var agentEvent in ResumeStreamAsync(run, approve, cancellationToken).ConfigureAwait(false))
        {
            if (agentEvent is AgentCompletedEvent completed)
            {
                result = completed.Result;
            }
        }

        return result!;
    }

    /// <summary>Resumes a suspended run with a human decision, streaming events as they happen.</summary>
    /// <param name="run">The suspension state (from <see cref="AgentResult.Suspension"/> or a store).</param>
    /// <param name="approve">Whether the gated calls may execute; denial informs the model instead.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    public async IAsyncEnumerable<AgentEvent> ResumeStreamAsync(
        SuspendedRun run,
        bool approve,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        var conversation = Conversation.Restore(new ConversationId(run.ConversationId), run.Messages);
        var guard = ToolCallGuard.Restore(_options.Rules, run.Guard);
        var resultsById = run.CompletedResults.ToDictionary(r => r.ToolUseId, StringComparer.Ordinal);

        foreach (var pendingCall in run.PendingApprovals)
        {
            ToolResultBlock result;
            if (approve)
            {
                var tool = Array.Find(_activeTools, t => t.Name == pendingCall.ToolName);
                var toolUse = new ToolUseBlock(pendingCall.ToolUseId, pendingCall.ToolName, pendingCall.Input);
                result = await ExecuteToolAsync(tool, violation: null, shadow: false, toolUse, cancellationToken)
                    .ConfigureAwait(false);
                if (tool is not null)
                {
                    guard.Record(tool, !result.IsError);
                }
            }
            else
            {
                result = new ToolResultBlock(
                    pendingCall.ToolUseId,
                    $"Denied: a human reviewer rejected the call to '{pendingCall.ToolName}'.",
                    IsError: true);
            }

            resultsById[pendingCall.ToolUseId] = result;
            yield return new AgentToolResultEvent(pendingCall.ToolUseId, pendingCall.ToolName, result.Content, result.IsError);
        }

        // Tool results must be ordered like the assistant's tool_use blocks.
        var ordered = conversation.Messages[^1].Content
            .OfType<ToolUseBlock>()
            .Select(use => (ContentBlock)resultsById[use.Id]);
        conversation = conversation.Append(new Message(MessageRole.User, [.. ordered]));

        await foreach (var agentEvent in RunLoopAsync(
            conversation, run.Usage, guard, [.. run.PlannedEffects], cancellationToken).ConfigureAwait(false))
        {
            yield return agentEvent;
        }
    }

    private async IAsyncEnumerable<AgentEvent> RunLoopAsync(
        Conversation conversation,
        AgentUsage usage,
        ToolCallGuard guard,
        List<PlannedEffect> plannedEffects,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        long startTimestamp = Stopwatch.GetTimestamp();
        using var runActivity = EmissaryDiagnostics.Source.StartActivity($"invoke_agent {_options.Model}");
        EmissaryDiagnostics.Tag(runActivity, "gen_ai.operation.name", "invoke_agent");
        EmissaryDiagnostics.Tag(runActivity, "gen_ai.request.model", _options.Model);

        var stopReason = AgentStopReason.TurnLimit;
        SuspendedRun? suspension = null;
        bool compactBeforeNextCall = false;
        for (int turn = 0; turn < _options.MaxTurns; turn++)
        {
            if (compactBeforeNextCall)
            {
                compactBeforeNextCall = false;
                var compaction = await CompactAsync(conversation, cancellationToken).ConfigureAwait(false);
                if (compaction is { } compacted)
                {
                    conversation = compacted.Conversation;
                    yield return new AgentCompactedEvent(compacted.MessagesSummarized, compacted.Summary);
                }
            }

            ModelResponse? response = null;
            var request = BuildRequest(conversation);
            using (var chatActivity = EmissaryDiagnostics.Source.StartActivity($"chat {_options.Model}"))
            {
                EmissaryDiagnostics.Tag(chatActivity, "gen_ai.operation.name", "chat");
                EmissaryDiagnostics.Tag(chatActivity, "gen_ai.request.model", _options.Model);

                await foreach (var streamEvent in _transport.StreamAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    switch (streamEvent)
                    {
                        case StreamTextDelta text:
                            yield return new AgentTextEvent(text.Text);
                            break;
                        case StreamThinkingDelta thinking:
                            yield return new AgentThinkingEvent(thinking.Text);
                            break;
                        case StreamToolUseStart toolUse:
                            yield return new AgentToolCallEvent(toolUse.Id, toolUse.Name);
                            break;
                        default:
                            response = ((StreamCompleted)streamEvent).Response;
                            break;
                    }
                }

                if (response is null)
                {
                    throw new InvalidOperationException("The transport stream ended without a StreamCompleted event.");
                }

                EmissaryDiagnostics.Tag(chatActivity, "gen_ai.usage.input_tokens", response.InputTokens);
                EmissaryDiagnostics.Tag(chatActivity, "gen_ai.usage.output_tokens", response.OutputTokens);
                EmissaryDiagnostics.Tag(chatActivity, "gen_ai.response.finish_reasons", new[] { response.StopReason });
            }

            RecordUsage(response);
            usage = usage.Add(
                response.InputTokens, response.OutputTokens,
                response.CacheCreationInputTokens, response.CacheReadInputTokens);

            if (_options.Compaction.TriggerInputTokens is { } trigger
                && response.InputTokens + response.CacheReadInputTokens > trigger)
            {
                compactBeforeNextCall = true;
            }
            var assistant = new Message(MessageRole.Assistant, response.Content);
            conversation = conversation.Append(assistant);
            yield return new AgentTurnEvent(assistant);

            if (_options.TokenBudget is { } budget && usage.InputTokens + usage.OutputTokens >= budget)
            {
                stopReason = AgentStopReason.BudgetExceeded;
                break;
            }

            if (response.StopReason == "tool_use")
            {
                var toolUses = response.Content.OfType<ToolUseBlock>().ToArray();
                var (results, pending) = await ExecuteToolsAsync(toolUses, guard, plannedEffects, cancellationToken).ConfigureAwait(false);
                for (int i = 0; i < results.Length; i++)
                {
                    if (results[i] is { } executed)
                    {
                        yield return new AgentToolResultEvent(
                            executed.ToolUseId, toolUses[i].Name, executed.Content, executed.IsError);
                    }
                }

                if (pending.Count > 0)
                {
                    suspension = new SuspendedRun(
                        conversation.Id.Value,
                        [.. conversation.Messages],
                        usage,
                        [.. results.OfType<ToolResultBlock>()],
                        pending,
                        guard.Snapshot(),
                        [.. plannedEffects]);
                    yield return new AgentSuspendedEvent(suspension);
                    stopReason = AgentStopReason.AwaitingApproval;
                    break;
                }

                conversation = conversation.Append(new Message(MessageRole.User, [.. results.OfType<ToolResultBlock>()]));
                continue;
            }

            stopReason = response.StopReason switch
            {
                "max_tokens" => AgentStopReason.MaxTokens,
                "refusal" => AgentStopReason.Refusal,
                _ => AgentStopReason.Completed,
            };
            break;
        }

        EmissaryDiagnostics.Tag(runActivity, "emissary.stop_reason", stopReason.ToString());
        EmissaryDiagnostics.RunDuration.Record(
            Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds,
            new KeyValuePair<string, object?>("gen_ai.request.model", _options.Model));

        yield return new AgentCompletedEvent(new AgentResult
        {
            Conversation = conversation,
            StopReason = stopReason,
            Usage = usage,
            Tainted = guard.Tainted,
            PlannedEffects = plannedEffects,
            Suspension = suspension,
        });
    }

    /// <summary>
    /// Undoes the effects of a completed run (saga compensation): every successfully executed
    /// tool with a <see cref="ToolDefinition.Compensation"/> handler is compensated with its
    /// original input, in reverse call order. Shadow-planned effects were never executed and are
    /// skipped.
    /// </summary>
    /// <param name="result">The run to unwind.</param>
    /// <param name="cancellationToken">Cancels the compensation pass.</param>
    public async Task<IReadOnlyList<CompensationResult>> CompensateAsync(
        AgentResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        var shadowed = result.PlannedEffects.Select(e => e.ToolUseId).ToHashSet(StringComparer.Ordinal);
        var succeeded = result.Conversation.Messages
            .SelectMany(m => m.Content)
            .OfType<ToolResultBlock>()
            .Where(r => !r.IsError)
            .Select(r => r.ToolUseId)
            .ToHashSet(StringComparer.Ordinal);

        var compensable = new List<(ToolUseBlock Use, ToolDefinition Tool)>();
        foreach (var toolUse in result.Conversation.Messages
            .Where(m => m.Role == MessageRole.Assistant)
            .SelectMany(m => m.Content)
            .OfType<ToolUseBlock>())
        {
            if (succeeded.Contains(toolUse.Id)
                && !shadowed.Contains(toolUse.Id)
                && _options.Tools.FirstOrDefault(t => t.Name == toolUse.Name) is { Compensation: not null } tool)
            {
                compensable.Add((toolUse, tool));
            }
        }

        var report = new List<CompensationResult>(compensable.Count);
        for (int i = compensable.Count - 1; i >= 0; i--)
        {
            var (use, tool) = compensable[i];
            using var activity = EmissaryDiagnostics.Source.StartActivity($"compensate_tool {use.Name}");
            EmissaryDiagnostics.Tag(activity, "gen_ai.tool.name", use.Name);
            try
            {
                string output = await tool.Compensation!(use.Input, cancellationToken).ConfigureAwait(false);
                report.Add(new CompensationResult(use.Name, use.Id, Success: true, output));
            }
            catch (ToolArgumentException exception)
            {
                EmissaryDiagnostics.Fail(activity, exception.Message);
                report.Add(new CompensationResult(use.Name, use.Id, Success: false, exception.Message));
            }
        }

        return report;
    }

    /// <summary>
    /// Summarizes the older part of the conversation with one extra model call and returns the
    /// compacted conversation, or <see langword="null"/> when there is nothing safe to compact.
    /// </summary>
    private async Task<(Conversation Conversation, int MessagesSummarized, string Summary)?> CompactAsync(
        Conversation conversation,
        CancellationToken cancellationToken)
    {
        if (ConversationCompactor.TryFindCutIndex(conversation.Messages, _options.Compaction.KeepRecentMessages)
            is not { } cutIndex)
        {
            return null;
        }

        using var activity = EmissaryDiagnostics.Source.StartActivity("compact_context");
        EmissaryDiagnostics.Tag(activity, "emissary.compaction.messages", cutIndex);

        string prompt = ConversationCompactor.BuildSummaryPrompt(
            conversation.Messages, cutIndex, _options.Compaction.SummaryInstruction);

        // A tool-free single-shot call: it is recorded in the trajectory like any other turn,
        // so a compacted run replays deterministically.
        var request = new ModelRequest(
            _options.Model, null, _options.MaxTokens, ThinkingMode.Disabled, _options.Effort,
            null, PromptCacheMode.None, [Message.User(prompt)], []);

        ModelResponse? response = null;
        await foreach (var streamEvent in _transport.StreamAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (streamEvent is StreamCompleted completed)
            {
                response = completed.Response;
            }
        }

        if (response is null)
        {
            throw new InvalidOperationException("The transport stream ended without a StreamCompleted event.");
        }

        string summary = string.Concat(response.Content.OfType<TextBlock>().Select(t => t.Text));
        return (ConversationCompactor.Apply(conversation, cutIndex, summary), cutIndex, summary);
    }

    private void RecordUsage(ModelResponse response)
    {
        var modelTag = new KeyValuePair<string, object?>("gen_ai.request.model", _options.Model);
        EmissaryDiagnostics.InputTokens.Add(response.InputTokens, modelTag);
        EmissaryDiagnostics.OutputTokens.Add(response.OutputTokens, modelTag);
        EmissaryDiagnostics.CacheCreationTokens.Add(response.CacheCreationInputTokens, modelTag);
        EmissaryDiagnostics.CacheReadTokens.Add(response.CacheReadInputTokens, modelTag);
    }

    private ModelRequest BuildRequest(Conversation conversation) => new(
        _options.Model,
        _options.SystemPrompt,
        _options.MaxTokens,
        _options.Thinking,
        _options.Effort,
        _options.OutputSchemaJson,
        _options.PromptCaching,
        [.. conversation.Messages],
        _activeTools,
        _options.WebSearch);

    private async Task<(ToolResultBlock?[] Results, List<PlannedEffect> Pending)> ExecuteToolsAsync(
        ToolUseBlock[] toolUses,
        ToolCallGuard guard,
        List<PlannedEffect> plannedEffects,
        CancellationToken cancellationToken)
    {
        // Guard checks run sequentially in tool-use order (state frozen for the batch);
        // permitted calls then execute in parallel; outcomes are recorded in order.
        // Approval-gated calls get no result — they suspend the run instead.
        var pending = new List<PlannedEffect>();
        var tools = new ToolDefinition?[toolUses.Length];
        var tasks = new Task<ToolResultBlock>?[toolUses.Length];
        for (int i = 0; i < toolUses.Length; i++)
        {
            tools[i] = Array.Find(_activeTools, t => t.Name == toolUses[i].Name);
            string? violation = tools[i] is { } tool ? guard.Check(tool) : null;

            if (violation is null
                && tools[i] is { } gated
                && _options.Mode == ExecutionMode.Live
                && _options.ApprovalRequired?.Invoke(gated) == true)
            {
                pending.Add(new PlannedEffect(toolUses[i].Name, toolUses[i].Id, toolUses[i].Input));
                continue;
            }

            bool shadow = violation is null
                && tools[i] is { Privileged: true }
                && _options.Mode == ExecutionMode.Shadow;
            if (shadow)
            {
                plannedEffects.Add(new PlannedEffect(toolUses[i].Name, toolUses[i].Id, toolUses[i].Input));
            }

            tasks[i] = ExecuteToolAsync(tools[i], violation, shadow, toolUses[i], cancellationToken);
        }

        var results = new ToolResultBlock?[toolUses.Length];
        for (int i = 0; i < tasks.Length; i++)
        {
            if (tasks[i] is { } task)
            {
                results[i] = await task.ConfigureAwait(false);
                if (tools[i] is { } tool)
                {
                    guard.Record(tool, !results[i]!.IsError);
                }
            }
        }

        return (results, pending);
    }

    private static async Task<ToolResultBlock> ExecuteToolAsync(
        ToolDefinition? tool,
        string? violation,
        bool shadow,
        ToolUseBlock toolUse,
        CancellationToken cancellationToken)
    {
        using var activity = EmissaryDiagnostics.Source.StartActivity($"execute_tool {toolUse.Name}");
        EmissaryDiagnostics.Tag(activity, "gen_ai.operation.name", "execute_tool");
        EmissaryDiagnostics.Tag(activity, "gen_ai.tool.name", toolUse.Name);
        EmissaryDiagnostics.ToolCalls.Add(1, new KeyValuePair<string, object?>("gen_ai.tool.name", toolUse.Name));

        if (tool is null)
        {
            EmissaryDiagnostics.Fail(activity, "unknown tool");
            return new ToolResultBlock(toolUse.Id, $"Unknown tool '{toolUse.Name}'.", IsError: true);
        }

        if (violation is not null)
        {
            EmissaryDiagnostics.Fail(activity, violation);
            return new ToolResultBlock(toolUse.Id, violation, IsError: true);
        }

        if (shadow)
        {
            EmissaryDiagnostics.Tag(activity, "emissary.shadow", true);
            return new ToolResultBlock(
                toolUse.Id,
                $"[shadow] Call to '{toolUse.Name}' was recorded as a planned effect and not executed.",
                IsError: false);
        }

        try
        {
            string content = await tool.Handler(toolUse.Input, cancellationToken).ConfigureAwait(false);
            return new ToolResultBlock(toolUse.Id, content, IsError: false);
        }
        catch (ToolArgumentException exception)
        {
            EmissaryDiagnostics.Fail(activity, exception.Message);
            return new ToolResultBlock(toolUse.Id, exception.Message, IsError: true);
        }
    }
}
