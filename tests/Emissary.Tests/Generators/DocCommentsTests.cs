using Emissary.SourceGenerators;

namespace Emissary.Tests;

public sealed class DocCommentsTests
{
    [Test]
    public async Task Normalize_handles_null_empty_and_multiline()
    {
        await Assert.That(DocComments.Normalize(null)).IsNull();
        await Assert.That(DocComments.Normalize("   \n  \n")).IsNull();
        await Assert.That(DocComments.Normalize("  first line \n second line \n")).IsEqualTo("first line second line");
    }

    [Test]
    public async Task Parse_returns_nothing_for_missing_or_malformed_xml()
    {
        await Assert.That(DocComments.Parse(null).Summary).IsNull();
        await Assert.That(DocComments.Parse("").Summary).IsNull();
        await Assert.That(DocComments.Parse("<!-- Badly formed XML comment ignored for member -->").Summary).IsNull();
    }

    [Test]
    public async Task Parse_extracts_summary_and_named_params()
    {
        var (summary, parameters) = DocComments.Parse(
            """<member name="M:X"><summary>Does things.</summary><param name="a">First.</param><param>No name.</param><param name="b">   </param></member>""");

        await Assert.That(summary).IsEqualTo("Does things.");
        await Assert.That(parameters.Count).IsEqualTo(1);
        await Assert.That(parameters["a"]).IsEqualTo("First.");
    }

    [Test]
    public async Task Parse_handles_member_without_summary()
    {
        var (summary, parameters) = DocComments.Parse(
            """<member name="M:X"><param name="a">First.</param></member>""");

        await Assert.That(summary).IsNull();
        await Assert.That(parameters["a"]).IsEqualTo("First.");
    }

    [Test]
    public async Task JsonEscape_covers_all_escape_shapes()
    {
        await Assert.That(DocComments.JsonEscape("say \"hi\"")).IsEqualTo("say \\\"hi\\\"");
        await Assert.That(DocComments.JsonEscape(@"a\b")).IsEqualTo(@"a\\b");
        await Assert.That(DocComments.JsonEscape("a\nb\rc\td")).IsEqualTo("a\\nb\\rc\\td");
        await Assert.That(DocComments.JsonEscape("a\u0001b")).IsEqualTo("a\\u0001b");
        await Assert.That(DocComments.JsonEscape("plain")).IsEqualTo("plain");
    }
}
