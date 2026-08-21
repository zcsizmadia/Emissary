using Emissary.SourceGenerators;

namespace Emissary.Tests;

public sealed class NameHelpersTests
{
    [Test]
    [Arguments("GetTemperature", "get_temperature")]
    [Arguments("echo", "echo")]
    [Arguments("HTTPGet", "h_t_t_p_get")]
    [Arguments("value2Json", "value2_json")]
    public async Task ToSnakeCase_converts(string input, string expected)
    {
        await Assert.That(NameHelpers.ToSnakeCase(input)).IsEqualTo(expected);
    }

    [Test]
    public async Task ToIdentifier_replaces_separators()
    {
        await Assert.That(NameHelpers.ToIdentifier("global::A.B.C")).IsEqualTo("global__A_B_C");
    }

    [Test]
    public async Task FormatDefault_covers_all_literal_shapes()
    {
        await Assert.That(NameHelpers.FormatDefault(null)).IsEqualTo("default");
        await Assert.That(NameHelpers.FormatDefault("x\"y")).IsEqualTo("\"x\\\"y\"");
        await Assert.That(NameHelpers.FormatDefault(true)).IsEqualTo("true");
        await Assert.That(NameHelpers.FormatDefault(false)).IsEqualTo("false");
        await Assert.That(NameHelpers.FormatDefault(7)).IsEqualTo("7");
        await Assert.That(NameHelpers.FormatDefault(9L)).IsEqualTo("9L");
        await Assert.That(NameHelpers.FormatDefault(double.NaN)).IsEqualTo("double.NaN");
        await Assert.That(NameHelpers.FormatDefault(double.PositiveInfinity)).IsEqualTo("double.PositiveInfinity");
        await Assert.That(NameHelpers.FormatDefault(double.NegativeInfinity)).IsEqualTo("double.NegativeInfinity");
        await Assert.That(NameHelpers.FormatDefault(1.5)).IsEqualTo("1.5D");
    }

    [Test]
    public async Task FormatDefault_formats_unknown_shapes_invariantly()
    {
        await Assert.That(NameHelpers.FormatDefault(2.5f)).IsEqualTo("2.5");
    }
}
