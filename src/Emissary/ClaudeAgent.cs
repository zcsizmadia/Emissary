using System.Collections.Immutable;
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
        if (options.Tools.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count() != options.Tools.Count)
        {
            throw new ArgumentException("Tool names must be unique.", nameof(options));
        }

        _options = options;
        _transport = transport;
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

        var usage = AgentUsage.Zero;
        var stopReason = AgentStopReason.TurnLimit;
        for (int turn = 0; turn < _options.MaxTurns; turn++)
        {
            ModelResponse? response = null;
            var request = BuildRequest(conversation);
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

            usage = usage.Add(response.InputTokens, response.OutputTokens);
            var assistant = new Message(MessageRole.Assistant, response.Content);
            conversation = conversation.Append(assistant);
            yield return new AgentTurnEvent(assistant);

            if (response.StopReason == "tool_use")
            {
                var toolUses = response.Content.OfType<ToolUseBlock>().ToArray();
                var results = await ExecuteToolsAsync(toolUses, cancellationToken).ConfigureAwait(false);
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

        yield return Complete(conversation, stopReason, usage);
    }

    private static AgentCompletedEvent Complete(Conversation conversation, AgentStopReason stopReason, AgentUsage usage) =>
        new(new AgentResult { Conversation = conversation, StopReason = stopReason, Usage = usage });

    private ModelRequest BuildRequest(Conversation conversation) => new(
        _options.Model,
        _options.SystemPrompt,
        _options.MaxTokens,
        _options.Thinking,
        _options.Effort,
        _options.OutputSchemaJson,
        [.. conversation.Messages],
        [.. _options.Tools]);

    private async Task<ToolResultBlock[]> ExecuteToolsAsync(
        ToolUseBlock[] toolUses,
        CancellationToken cancellationToken)
    {
        var tasks = new Task<ToolResultBlock>[toolUses.Length];
        for (int i = 0; i < toolUses.Length; i++)
        {
            tasks[i] = ExecuteToolAsync(toolUses[i], cancellationToken);
        }

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<ToolResultBlock> ExecuteToolAsync(ToolUseBlock toolUse, CancellationToken cancellationToken)
    {
        var tool = _options.Tools.FirstOrDefault(t => t.Name == toolUse.Name);
        if (tool is null)
        {
            return new ToolResultBlock(toolUse.Id, $"Unknown tool '{toolUse.Name}'.", IsError: true);
        }

        try
        {
            string content = await tool.Handler(toolUse.Input, cancellationToken).ConfigureAwait(false);
            return new ToolResultBlock(toolUse.Id, content, IsError: false);
        }
        catch (ToolArgumentException exception)
        {
            return new ToolResultBlock(toolUse.Id, exception.Message, IsError: true);
        }
    }
}
