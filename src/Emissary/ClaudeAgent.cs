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
        : this(options, new AnthropicTransport(options?.ApiKey))
    {
    }

    /// <summary>Creates an agent talking to the Claude API, recording every exchange.</summary>
    /// <param name="options">The agent configuration.</param>
    /// <param name="recorder">Receives every completed model exchange.</param>
    public ClaudeAgent(AgentOptions options, TrajectoryRecorder recorder)
        : this(options, new RecordingTransport(new AnthropicTransport(options?.ApiKey), recorder))
    {
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
    public async IAsyncEnumerable<AgentEvent> StreamAsync(
        Conversation conversation,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        long startTimestamp = Stopwatch.GetTimestamp();
        using var runActivity = EmissaryDiagnostics.Source.StartActivity($"invoke_agent {_options.Model}");
        EmissaryDiagnostics.Tag(runActivity, "gen_ai.operation.name", "invoke_agent");
        EmissaryDiagnostics.Tag(runActivity, "gen_ai.request.model", _options.Model);

        var usage = AgentUsage.Zero;
        var stopReason = AgentStopReason.TurnLimit;
        var guard = new ToolCallGuard(_options.Rules);
        for (int turn = 0; turn < _options.MaxTurns; turn++)
        {
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
                var results = await ExecuteToolsAsync(toolUses, guard, cancellationToken).ConfigureAwait(false);
                for (int i = 0; i < results.Length; i++)
                {
                    yield return new AgentToolResultEvent(
                        results[i].ToolUseId, toolUses[i].Name, results[i].Content, results[i].IsError);
                }

                conversation = conversation.Append(new Message(MessageRole.User, [.. results]));
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
        });
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
        _activeTools);

    private async Task<ToolResultBlock[]> ExecuteToolsAsync(
        ToolUseBlock[] toolUses,
        ToolCallGuard guard,
        CancellationToken cancellationToken)
    {
        // Guard checks run sequentially in tool-use order (state frozen for the batch);
        // permitted calls then execute in parallel; outcomes are recorded in order.
        var tools = new ToolDefinition?[toolUses.Length];
        var tasks = new Task<ToolResultBlock>[toolUses.Length];
        for (int i = 0; i < toolUses.Length; i++)
        {
            tools[i] = Array.Find(_activeTools, t => t.Name == toolUses[i].Name);
            string? violation = tools[i] is { } tool ? guard.Check(tool) : null;
            tasks[i] = ExecuteToolAsync(tools[i], violation, toolUses[i], cancellationToken);
        }

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        for (int i = 0; i < results.Length; i++)
        {
            if (tools[i] is { } tool)
            {
                guard.Record(tool, !results[i].IsError);
            }
        }

        return results;
    }

    private static async Task<ToolResultBlock> ExecuteToolAsync(
        ToolDefinition? tool,
        string? violation,
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
