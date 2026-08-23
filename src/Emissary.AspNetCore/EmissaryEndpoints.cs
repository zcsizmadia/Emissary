using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Emissary.AspNetCore;

/// <summary>Maps Emissary agents onto ASP.NET Core endpoints.</summary>
public static class EmissaryEndpoints
{
    /// <summary>
    /// Maps a POST endpoint that runs the DI-registered <see cref="ClaudeAgent"/> on
    /// <c>{"message": "..."}</c> and streams the run as Server-Sent Events
    /// (<c>text</c>, <c>thinking</c>, <c>tool_call</c>, <c>tool_result</c>, <c>suspended</c>,
    /// <c>completed</c>). A suspension is saved to the DI-registered
    /// <see cref="IAgentStateStore"/> when one is present.
    /// </summary>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="pattern">The route pattern, e.g. <c>"/agent"</c>.</param>
    public static IEndpointConventionBuilder MapEmissaryAgent(this IEndpointRouteBuilder endpoints, string pattern)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints.MapPost(pattern, HandleAgentAsync);
    }

    /// <summary>
    /// Maps the approval webhook: POST <c>{"conversationId": "...", "approve": true}</c> loads the
    /// suspended run from the <see cref="IAgentStateStore"/>, resumes it with the decision, and
    /// streams the rest of the run as Server-Sent Events.
    /// </summary>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="pattern">The route pattern, e.g. <c>"/agent/approvals"</c>.</param>
    public static IEndpointConventionBuilder MapEmissaryApprovals(this IEndpointRouteBuilder endpoints, string pattern)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints.MapPost(pattern, HandleApprovalAsync);
    }

    private static async Task HandleAgentAsync(HttpContext context)
    {
        var request = await JsonSerializer.DeserializeAsync(
            context.Request.Body, EmissaryWireContext.Default.AgentMessageRequest, context.RequestAborted)
            .ConfigureAwait(false);
        if (request?.Message is not { Length: > 0 } message)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var agent = context.RequestServices.GetRequiredService<ClaudeAgent>();
        await StreamEventsAsync(context, agent.StreamAsync(message, context.RequestAborted)).ConfigureAwait(false);
    }

    private static async Task HandleApprovalAsync(HttpContext context)
    {
        var request = await JsonSerializer.DeserializeAsync(
            context.Request.Body, EmissaryWireContext.Default.ApprovalRequest, context.RequestAborted)
            .ConfigureAwait(false);
        if (request is null || request.ConversationId == Guid.Empty)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var store = context.RequestServices.GetRequiredService<IAgentStateStore>();
        var run = await store.LoadAsync(request.ConversationId, context.RequestAborted).ConfigureAwait(false);
        if (run is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Claim the run before resuming: resuming executes the privileged call a human just
        // approved, and two approvals arriving together must not both run it. Whoever deletes it
        // owns it; the loser gets 409 rather than a second execution.
        if (!await store.DeleteAsync(request.ConversationId, CancellationToken.None).ConfigureAwait(false))
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        var agent = context.RequestServices.GetRequiredService<ClaudeAgent>();
        await StreamEventsAsync(context, agent.ResumeStreamAsync(run, request.Approve, context.RequestAborted))
            .ConfigureAwait(false);
    }

    private static async Task StreamEventsAsync(HttpContext context, IAsyncEnumerable<AgentEvent> events)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";

        // Without this, a stock nginx (or any buffering proxy) holds every event and delivers the
        // whole run in one burst at the end, which defeats the point of streaming.
        context.Response.Headers["X-Accel-Buffering"] = "no";
        // Required rather than optional: this feature backs HttpResponse.Body, so a server that
        // lacks it could not have produced a response at all.
        context.Features.GetRequiredFeature<IHttpResponseBodyFeature>().DisableBuffering();

        var store = context.RequestServices.GetService<IAgentStateStore>();

        try
        {
            await StreamBodyAsync(context, events, store).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client hung up. Normal, and there is nobody left to tell.
        }
        catch (Exception exception)
        {
            // The response headers are long since committed, so an exception here cannot become a
            // 500 — without an error event the caller just sees the connection die mid-answer and
            // cannot tell that from a completed run. The type only, per ADR 0007.
            await WriteAsync(context, "error",
                JsonSerializer.Serialize(
                    new ErrorDto(exception.GetType().Name), EmissaryWireContext.Default.ErrorDto))
                .ConfigureAwait(false);
        }
    }

    private static async Task StreamBodyAsync(
        HttpContext context,
        IAsyncEnumerable<AgentEvent> events,
        IAgentStateStore? store)
    {
        await foreach (var agentEvent in events.ConfigureAwait(false))
        {
            switch (agentEvent)
            {
                case AgentTextEvent text:
                    await WriteAsync(context, "text",
                        JsonSerializer.Serialize(new DeltaDto(text.Delta), EmissaryWireContext.Default.DeltaDto))
                        .ConfigureAwait(false);
                    break;
                case AgentThinkingEvent thinking:
                    await WriteAsync(context, "thinking",
                        JsonSerializer.Serialize(new DeltaDto(thinking.Delta), EmissaryWireContext.Default.DeltaDto))
                        .ConfigureAwait(false);
                    break;
                case AgentToolCallEvent call:
                    await WriteAsync(context, "tool_call",
                        JsonSerializer.Serialize(new ToolCallDto(call.Id, call.Name), EmissaryWireContext.Default.ToolCallDto))
                        .ConfigureAwait(false);
                    break;
                case AgentToolResultEvent result:
                    await WriteAsync(context, "tool_result",
                        JsonSerializer.Serialize(
                            new ToolResultDto(result.Id, result.Name, result.Result, result.IsError),
                            EmissaryWireContext.Default.ToolResultDto))
                        .ConfigureAwait(false);
                    break;
                case AgentToolFailedEvent failed:
                    await WriteAsync(context, "tool_failed",
                        JsonSerializer.Serialize(
                            new ToolFailedDto(
                                failed.Failure.ToolUseId,
                                failed.Failure.ToolName,
                                failed.Failure.Exception.GetType().Name,
                                failed.Failure.TimedOut),
                            EmissaryWireContext.Default.ToolFailedDto))
                        .ConfigureAwait(false);
                    break;
                case AgentHandoffEvent handoff:
                    await WriteAsync(context, "handoff",
                        JsonSerializer.Serialize(
                            new HandoffDto(handoff.TargetName, handoff.Reason),
                            EmissaryWireContext.Default.HandoffDto))
                        .ConfigureAwait(false);
                    break;
                case AgentSuspendedEvent suspended:
                    if (store is not null)
                    {
                        // Never the request token: a client that disconnects at the moment of
                        // suspension — the likeliest moment, since the agent has just gone quiet —
                        // would cancel the save and lose the run, and the approval webhook would
                        // then 404 forever.
                        await store.SaveAsync(suspended.Suspension, CancellationToken.None).ConfigureAwait(false);
                    }

                    await WriteAsync(context, "suspended",
                        JsonSerializer.Serialize(
                            new SuspendedDto(
                                suspended.Suspension.ConversationId,
                                [.. suspended.Suspension.PendingApprovals.Select(p => p.ToolName)]),
                            EmissaryWireContext.Default.SuspendedDto))
                        .ConfigureAwait(false);
                    break;
                case AgentCompletedEvent completed:
                    await WriteAsync(context, "completed",
                        JsonSerializer.Serialize(
                            new CompletedDto(
                                completed.Result.Conversation.Id.Value,
                                completed.Result.StopReason.ToString(),
                                completed.Result.FinalText,
                                completed.Result.Usage.InputTokens,
                                completed.Result.Usage.OutputTokens,
                                completed.Result.Tainted),
                            EmissaryWireContext.Default.CompletedDto))
                        .ConfigureAwait(false);
                    break;
                default:
                    break;
            }
        }
    }

    private static async Task WriteAsync(HttpContext context, string eventName, string json)
    {
        await context.Response.WriteAsync($"event: {eventName}\ndata: {json}\n\n", context.RequestAborted)
            .ConfigureAwait(false);
        await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
    }
}
