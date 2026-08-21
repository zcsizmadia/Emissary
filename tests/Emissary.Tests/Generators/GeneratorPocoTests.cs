using Emissary.Tests.Generators;

namespace Emissary.Tests;

public sealed class GeneratorPocoTests
{
    [Test]
    public async Task Same_poco_used_twice_emits_one_binder()
    {
        var result = await GeneratorHarness.RunClean("""
            public sealed record Point(int X, int Y);

            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d")]
                public static string Line(Point start, Point end) => "";
            }
            """);

        string source = GeneratorHarness.GeneratedSource(result);
        int binderCount = source.Split(["static global::Point __EmissaryBind_Point"], StringSplitOptions.None).Length - 1;
        await Assert.That(binderCount).IsEqualTo(1);
    }

    [Test]
    public async Task Poco_members_can_use_enums_and_arrays()
    {
        var result = await GeneratorHarness.RunClean("""
            public enum Mode { Fast, Slow }

            public sealed record Job(Mode Mode, string[] Steps);

            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d")]
                public static string Run(Job job) => "";
            }
            """);

        string source = GeneratorHarness.GeneratedSource(result);
        await Assert.That(source).Contains("__EmissaryParse_Mode");
        await Assert.That(source).Contains("__EmissaryReadArray_String");
    }

    [Test]
    public async Task Property_mode_poco_binds_only_public_writable_properties()
    {
        var result = await GeneratorHarness.RunClean("""
            public sealed class Settings
            {
                public required string Theme { get; set; }
                public int Size { get; init; }
                public string ReadOnly { get; } = "";
                public string PrivateSet { get; private set; } = "";
                public static string Shared { get; set; } = "";
                private string Hidden { get; set; } = "";
                public string this[int index] { get => ""; set { } }
            }

            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d")]
                public static string Use(Settings settings) => "";
            }
            """);

        string source = GeneratorHarness.GeneratedSource(result);
        await Assert.That(source).Contains("{ Theme = __v_Theme, Size = __v_Size }");
        await Assert.That(source).DoesNotContain("__v_ReadOnly");
        await Assert.That(source).DoesNotContain("__v_PrivateSet");
        await Assert.That(source).DoesNotContain("__v_Shared");
        await Assert.That(source).DoesNotContain("__v_Hidden");
        await Assert.That(source).Contains("\\\"required\\\":[\\\"theme\\\"]");
    }

    [Test]
    public async Task Property_mode_poco_with_unsupported_property_type_is_an_error()
    {
        var result = GeneratorHarness.Run("""
            public sealed class Event
            {
                public string Name { get; set; } = "";
                public System.DateTime When { get; set; }
            }

            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d")]
                public static string Use(Event value) => "";
            }
            """, out _);

        await Assert.That(GeneratorHarness.DiagnosticIds(result)).Contains("EMS002");
        await Assert.That(result.GeneratedSources.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Nested_poco_member_with_optional_default_binds_recursively()
    {
        var result = await GeneratorHarness.RunClean("""
            public sealed record Inner(string Name);

            public sealed record Outer(Inner Inner, int Count = 2);

            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d")]
                public static string Use(Outer outer) => "";
            }
            """);

        string source = GeneratorHarness.GeneratedSource(result);
        await Assert.That(source).Contains("__EmissaryBind_Outer");
        await Assert.That(source).Contains("__EmissaryBind_Inner");
        await Assert.That(source).Contains(": 2;");
        await Assert.That(source).Contains("is missing required member 'name'");
    }

    [Test]
    public async Task Interface_parameter_is_an_error()
    {
        var result = GeneratorHarness.Run("""
            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d")]
                public static string Use(System.IComparable value) => "";
            }
            """, out _);

        await Assert.That(GeneratorHarness.DiagnosticIds(result)).Contains("EMS002");
    }

    [Test]
    public async Task Cyclic_poco_is_an_error()
    {
        var result = GeneratorHarness.Run("""
            public sealed record Node(string Name, Node Next);

            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d")]
                public static string Walk(Node node) => "";
            }
            """, out _);

        await Assert.That(GeneratorHarness.DiagnosticIds(result)).Contains("EMS002");
    }

    [Test]
    public async Task Multiple_parameterized_constructors_are_an_error()
    {
        var result = GeneratorHarness.Run("""
            public sealed class Ambiguous
            {
                public Ambiguous(string a) { }
                public Ambiguous(int b) { }
            }

            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d")]
                public static string Use(Ambiguous value) => "";
            }
            """, out _);

        await Assert.That(GeneratorHarness.DiagnosticIds(result)).Contains("EMS002");
    }

    [Test]
    public async Task Private_constructor_only_is_an_error()
    {
        var result = GeneratorHarness.Run("""
            public sealed class Hidden
            {
                private Hidden() { }
                public string Name { get; set; } = "";
            }

            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d")]
                public static string Use(Hidden value) => "";
            }
            """, out _);

        await Assert.That(GeneratorHarness.DiagnosticIds(result)).Contains("EMS002");
    }

    [Test]
    public async Task Abstract_type_is_an_error()
    {
        var result = GeneratorHarness.Run("""
            public abstract class Base
            {
                public string Name { get; set; } = "";
            }

            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d")]
                public static string Use(Base value) => "";
            }
            """, out _);

        await Assert.That(GeneratorHarness.DiagnosticIds(result)).Contains("EMS002");
    }

    [Test]
    public async Task Generic_poco_is_an_error()
    {
        var result = GeneratorHarness.Run("""
            public sealed record Box<T>(T Value);

            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d")]
                public static string Use(Box<string> box) => "";
            }
            """, out _);

        await Assert.That(GeneratorHarness.DiagnosticIds(result)).Contains("EMS002");
    }

    [Test]
    public async Task Poco_without_bindable_members_is_an_error()
    {
        var result = GeneratorHarness.Run("""
            public sealed class Empty
            {
            }

            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d")]
                public static string Use(Empty value) => "";
            }
            """, out _);

        await Assert.That(GeneratorHarness.DiagnosticIds(result)).Contains("EMS002");
    }

    [Test]
    public async Task Unsupported_member_is_reported_with_owner_and_name()
    {
        var result = GeneratorHarness.Run("""
            public sealed record Event(string Name, System.DateTime When);

            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d")]
                public static string Use(Event value) => "";
            }
            """, out _);

        var diagnostics = result.Diagnostics.Where(d => d.Id == "EMS002").ToList();
        await Assert.That(diagnostics.Any(d => d.GetMessage(null).Contains("'Event.When'"))).IsTrue();
    }

    [Test]
    public async Task Optional_poco_parameter_defaults_to_null()
    {
        var result = await GeneratorHarness.RunClean("""
            public sealed record Point(int X, int Y);

            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d")]
                public static string Use(string name, Point point = null) => name;
            }
            """);

        await Assert.That(GeneratorHarness.GeneratedSource(result)).Contains(": default;");
    }

    [Test]
    public async Task Attribute_description_overrides_xml_summary()
    {
        var result = await GeneratorHarness.RunClean("""
            public static partial class Tools
            {
                /// <summary>From the docs.</summary>
                [Emissary.ClaudeTool(Description = "From the attribute.")]
                public static string Echo(string text) => text;
            }
            """);

        string source = GeneratorHarness.GeneratedSource(result);
        await Assert.That(source).Contains("\"From the attribute.\"");
        await Assert.That(source).DoesNotContain("From the docs.");
    }

    [Test]
    public async Task Xml_summary_is_used_when_attribute_has_no_description()
    {
        var result = await GeneratorHarness.RunClean("""
            public static partial class Tools
            {
                /// <summary>
                /// Echoes
                /// the text.
                /// </summary>
                /// <param name="text">The text to echo, say "hi".</param>
                [Emissary.ClaudeTool]
                public static string Echo(string text) => text;
            }
            """);

        await Assert.That(GeneratorHarness.DiagnosticIds(result)).DoesNotContain("EMS001");
        string source = GeneratorHarness.GeneratedSource(result);
        await Assert.That(source).Contains("\"Echoes the text.\"");
        await Assert.That(source).Contains("description");
    }

    [Test]
    public async Task Malformed_xml_doc_still_raises_EMS001()
    {
        var result = GeneratorHarness.Run("""
            public static partial class Tools
            {
                /// <summary>Unclosed
                [Emissary.ClaudeTool]
                public static string Echo(string text) => text;
            }
            """, out _);

        await Assert.That(GeneratorHarness.DiagnosticIds(result)).Contains("EMS001");
    }
}
