using System.Text.Json;

namespace Emissary.Tests;

public sealed class PartialJsonTests
{
    private static string? Complete(string prefix) => PartialJson.TryComplete(prefix);

    private static bool Parses(string? json)
    {
        if (json is null)
        {
            return false;
        }

        using var _ = JsonDocument.Parse(json);
        return true;
    }

    [Test]
    public async Task A_complete_document_passes_through()
    {
        await Assert.That(Complete("""{"title":"done"}""")).IsEqualTo("""{"title":"done"}""");
    }

    [Test]
    public async Task Nothing_yet_yields_nothing()
    {
        await Assert.That(Complete("")).IsNull();
        await Assert.That(Complete("   ")).IsNull();
    }

    [Test]
    public async Task An_open_object_is_closed()
    {
        await Assert.That(Complete("""{"title":"done" """.TrimEnd())).IsEqualTo("""{"title":"done"}""");
    }

    [Test]
    public async Task A_value_string_mid_flight_is_closed()
    {
        string? completed = Complete("""{"title":"in prog""");

        await Assert.That(completed).IsEqualTo("""{"title":"in prog"}""");
        await Assert.That(Parses(completed)).IsTrue();
    }

    [Test]
    public async Task A_half_written_key_is_dropped()
    {
        // {"title":"x","sev  -> the dangling key cannot become a member, so it is discarded.
        string? completed = Complete("""{"title":"x","sev""");

        await Assert.That(completed).IsEqualTo("""{"title":"x"}""");
        await Assert.That(Parses(completed)).IsTrue();
    }

    [Test]
    public async Task A_key_awaiting_its_value_is_dropped()
    {
        await Assert.That(Complete("""{"title":"x","severity":""")).IsEqualTo("""{"title":"x"}""");
        await Assert.That(Complete("""{"title":"x","severity": """)).IsEqualTo("""{"title":"x"}""");
    }

    [Test]
    public async Task A_partial_literal_is_dropped()
    {
        await Assert.That(Complete("""{"title":"x","ok":tru""")).IsEqualTo("""{"title":"x"}""");
    }

    [Test]
    public async Task A_trailing_comma_is_removed()
    {
        await Assert.That(Complete("""{"title":"x", """.TrimEnd())).IsEqualTo("""{"title":"x"}""");
    }

    [Test]
    public async Task Nested_objects_and_arrays_are_closed_in_order()
    {
        string? completed = Complete("""{"a":{"b":[1,2""");

        await Assert.That(completed).IsEqualTo("""{"a":{"b":[1,2]}}""");
        await Assert.That(Parses(completed)).IsTrue();
    }

    [Test]
    public async Task An_array_of_strings_mid_element_is_closed()
    {
        string? completed = Complete("""{"tags":["fast","nat""");

        await Assert.That(completed).IsEqualTo("""{"tags":["fast","nat"]}""");
    }

    [Test]
    public async Task An_empty_container_is_closed()
    {
        await Assert.That(Complete("{")).IsEqualTo("{}");
        await Assert.That(Complete("""{"tags":[""")).IsEqualTo("""{"tags":[]}""");
    }

    [Test]
    public async Task Escapes_inside_strings_do_not_confuse_the_scanner()
    {
        // The escaped quote must not be read as closing the string.
        string? completed = Complete("""{"title":"say \"hi""");

        await Assert.That(completed).IsEqualTo("""{"title":"say \"hi"}""");
        await Assert.That(Parses(completed)).IsTrue();
    }

    [Test]
    public async Task Braces_inside_strings_are_not_containers()
    {
        string? completed = Complete("""{"title":"a { b [ c""");

        await Assert.That(completed).IsEqualTo("""{"title":"a { b [ c"}""");
        await Assert.That(Parses(completed)).IsTrue();
    }

    [Test]
    public async Task Malformed_input_never_produces_unparseable_output()
    {
        // The contract covers prefixes of valid documents; for anything else the guarantee is
        // simply that nothing unparseable is handed back (garbage in, nothing or empty out).
        await Assert.That(Complete("not json")).IsNull();
        await Assert.That(Complete("[[[")).IsEqualTo("[[[]]]");

        // Includes cases where the fallback cannot be closed at all ({"a":1,,) and where it closes
        // but still will not parse because the prefix was already malformed ({"a" 1,"b").
        foreach (string garbage in new[] { "{,", "}", ":", ",,,", "{\"a\":}", "{\"a\":1,,", "{\"a\" 1,\"b\"" })
        {
            string? completed = Complete(garbage);
            if (completed is not null)
            {
                await Assert.That(Parses(completed)).IsTrue();
            }
        }
    }

    [Test]
    public async Task Every_prefix_of_a_real_document_is_either_null_or_valid_json()
    {
        const string document = """{"title":"500 on checkout","severity":"Critical","tags":["checkout","regression"],"count":42}""";

        for (int i = 0; i <= document.Length; i++)
        {
            string? completed = Complete(document[..i]);
            if (completed is not null)
            {
                // The invariant that matters: we never hand back unparseable JSON.
                await Assert.That(Parses(completed)).IsTrue();
            }
        }
    }
}
