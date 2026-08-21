using System.Runtime.CompilerServices;
using System.Text.Json;
using Emissary.Transport;

namespace Emissary.Tests.Agents;

/// <summary>Scripted transport: each enqueued turn is the event stream for one model call.</summary>
internal sealed class FakeTransport : IModelTransport
{
    private readonly Queue<StreamEvent[]> _turns = new();

    public List<ModelRequest> Requests { get; } = [];

    public void EnqueueTurn(params StreamEvent[] events) => _turns.Enqueue(events);

    public async IAsyncEnumerable<StreamEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Requests.Add(request);
        foreach (var streamEvent in _turns.Dequeue())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return streamEvent;
        }
    }

    public static StreamCompleted TextTurn(string text, string stopReason = "end_turn", long input = 10, long output = 5) =>
        new(new ModelResponse([new TextBlock(text)], stopReason, input, output));

    public static StreamCompleted ToolTurn(params ToolUseBlock[] uses) =>
        new(new ModelResponse([.. uses], "tool_use", 10, 5));

    public static ToolUseBlock Use(string id, string name, string inputJson)
    {
        using var document = JsonDocument.Parse(inputJson);
        return new ToolUseBlock(id, name, document.RootElement.Clone());
    }
}
