using System.Text.Json.Serialization;
using Emissary.Tests.Agents;
using Emissary.Transport;

namespace Emissary.Tests;

internal sealed record Triage(string Title, TriageSeverity Severity, string[] Tags);

internal enum TriageSeverity
{
    Low,
    Critical,
}

// Emissary's generated schemas express enums as their member names, so the matching
// serializer context reads them as strings.
[JsonSerializable(typeof(Triage))]
[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
internal sealed partial class StreamingJsonContext : JsonSerializerContext;

public sealed class StreamingOutputTests
{
    private const string Document =
        """{"Title":"500 on checkout","Severity":"Critical","Tags":["checkout","regression"]}""";

    /// <summary>Streams the document in fixed-size chunks, as the API would.</summary>
    private static ClaudeAgent AgentStreaming(string document, int chunkSize)
    {
        var events = new List<StreamEvent>();
        for (int i = 0; i < document.Length; i += chunkSize)
        {
            events.Add(new StreamTextDelta(document.Substring(i, Math.Min(chunkSize, document.Length - i))));
        }

        events.Add(new StreamCompleted(new ModelResponse([new TextBlock(document)], "end_turn", 10, 20)));

        var transport = new FakeTransport();
        transport.EnqueueTurn([.. events]);
        return new ClaudeAgent(new AgentOptions { OutputSchemaJson = "{}" }, transport);
    }

    [Test]
    public async Task Partial_values_arrive_as_the_document_streams()
    {
        var agent = AgentStreaming(Document, chunkSize: 8);

        var seen = new List<Triage>();
        await foreach (var partial in agent.StreamAsync("triage this", StreamingJsonContext.Default.Triage))
        {
            seen.Add(partial);
        }

        // More than one update, and the last one is the complete answer.
        await Assert.That(seen.Count).IsGreaterThan(1);
        await Assert.That(seen[^1].Title).IsEqualTo("500 on checkout");
        await Assert.That(seen[^1].Severity).IsEqualTo(TriageSeverity.Critical);
        await Assert.That(seen[^1].Tags).IsEquivalentTo(["checkout", "regression"]);
    }

    [Test]
    public async Task Updates_only_ever_grow_toward_the_final_answer()
    {
        var agent = AgentStreaming(Document, chunkSize: 5);

        var seen = new List<Triage>();
        await foreach (var partial in agent.StreamAsync("triage this", StreamingJsonContext.Default.Triage))
        {
            seen.Add(partial);
        }

        // Properties that have not arrived yet are null even though the record declares them
        // non-nullable — a partial is a progress snapshot, which the API documents explicitly.
        await Assert.That(seen.Any(t => t.Title is null || t.Tags is null)).IsTrue();

        // Title fills in before tags, and every partial title is a prefix of the final one:
        // updates only ever grow, never contradict.
        await Assert.That(seen.Any(t => t.Title is { Length: > 0 } && (t.Tags is null || t.Tags.Length == 0))).IsTrue();
        foreach (var partial in seen.Where(t => t.Title is { Length: > 0 }))
        {
            await Assert.That("500 on checkout".StartsWith(partial.Title, StringComparison.Ordinal)).IsTrue();
        }
    }

    [Test]
    public async Task A_single_chunk_still_yields_the_answer()
    {
        var agent = AgentStreaming(Document, chunkSize: Document.Length);

        var seen = new List<Triage>();
        await foreach (var partial in agent.StreamAsync("triage this", StreamingJsonContext.Default.Triage))
        {
            seen.Add(partial);
        }

        await Assert.That(seen.Count).IsEqualTo(1);
        await Assert.That(seen[0].Title).IsEqualTo("500 on checkout");
    }

    [Test]
    public async Task Partially_spelled_enum_values_are_skipped_not_surfaced()
    {
        // "Criti" is valid JSON but not a valid TriageSeverity: it must never be yielded.
        var agent = AgentStreaming(Document, chunkSize: 1);

        await foreach (var partial in agent.StreamAsync("triage this", StreamingJsonContext.Default.Triage))
        {
            await Assert.That(Enum.IsDefined(partial.Severity)).IsTrue();
        }
    }

    [Test]
    public async Task Non_text_events_do_not_disturb_the_stream()
    {
        var transport = new FakeTransport();
        transport.EnqueueTurn(
            new StreamThinkingDelta("considering"),
            new StreamTextDelta(Document),
            new StreamCompleted(new ModelResponse([new TextBlock(Document)], "end_turn", 5, 5)));
        var agent = new ClaudeAgent(new AgentOptions(), transport);

        var seen = new List<Triage>();
        await foreach (var partial in agent.StreamAsync("go", StreamingJsonContext.Default.Triage))
        {
            seen.Add(partial);
        }

        await Assert.That(seen.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_non_json_answer_yields_nothing()
    {
        var transport = new FakeTransport();
        transport.EnqueueTurn(
            new StreamTextDelta("I cannot help with that."),
            FakeTransport.TextTurn("I cannot help with that."));
        var agent = new ClaudeAgent(new AgentOptions(), transport);

        var seen = new List<Triage>();
        await foreach (var partial in agent.StreamAsync("go", StreamingJsonContext.Default.Triage))
        {
            seen.Add(partial);
        }

        await Assert.That(seen.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Type_info_is_required()
    {
        var agent = new ClaudeAgent(new AgentOptions(), new FakeTransport());

        await Assert.That(async () =>
            {
                await foreach (var _ in agent.StreamAsync<Triage>("go", null!))
                {
                }
            })
            .Throws<ArgumentNullException>();
    }
}
