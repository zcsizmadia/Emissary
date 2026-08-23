using Emissary.Tests.Generators;

namespace Emissary.Tests;

public sealed class GeneratorEmissionTests
{
    [Test]
    public async Task Global_namespace_tool_emits_without_namespace_block()
    {
        var result = await GeneratorHarness.RunClean("""
            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d")]
                public static string Echo(string text) => text;
            }
            """);

        string source = GeneratorHarness.GeneratedSource(result);
        await Assert.That(source).DoesNotContain("namespace");
        await Assert.That(source).Contains("public static global::Emissary.ToolDefinition EchoTool");
    }

    [Test]
    public async Task Namespaced_nested_record_struct_containers_are_reproduced()
    {
        var result = await GeneratorHarness.RunClean("""
            namespace Deep.Nesting;

            public partial record struct Outer
            {
                public partial struct Middle
                {
                    public partial record Inner
                    {
                        [Emissary.ClaudeTool(Description = "d")]
                        public static string Echo(string text) => text;
                    }
                }
            }
            """);

        string source = GeneratorHarness.GeneratedSource(result);
        await Assert.That(source).Contains("namespace Deep.Nesting");
        await Assert.That(source).Contains("partial record struct Outer");
        await Assert.That(source).Contains("partial struct Middle");
        await Assert.That(source).Contains("partial record Inner");
    }

    [Test]
    public async Task Name_override_is_used_verbatim()
    {
        var result = await GeneratorHarness.RunClean("""
            public static partial class Tools
            {
                [Emissary.ClaudeTool(Name = "custom_name", Description = "d")]
                public static string DoTheThing(string input) => input;
            }
            """);

        await Assert.That(GeneratorHarness.GeneratedSource(result)).Contains("\"custom_name\"");
    }

    [Test]
    public async Task Empty_name_override_falls_back_to_snake_case()
    {
        var result = await GeneratorHarness.RunClean("""
            public static partial class Tools
            {
                [Emissary.ClaudeTool(Name = "", Description = "d")]
                public static string DoTheThing(string input) => input;
            }
            """);

        await Assert.That(GeneratorHarness.GeneratedSource(result)).Contains("\"do_the_thing\"");
    }

    [Test]
    public async Task Parameterless_tool_has_empty_schema_without_required()
    {
        var result = await GeneratorHarness.RunClean("""
            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d")]
                public static string Now() => "now";
            }
            """);

        await Assert.That(GeneratorHarness.GeneratedSource(result))
            .Contains("""{\"type\":\"object\",\"properties\":{}}""");
    }

    [Test]
    public async Task Default_literals_cover_all_primitive_shapes()
    {
        var result = await GeneratorHarness.RunClean("""
            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d")]
                public static string Defaults(
                    string name = "x",
                    string missing = null,
                    bool flag = true,
                    int count = 7,
                    long big = 9L,
                    double nan = double.NaN,
                    double positive = double.PositiveInfinity,
                    double negative = double.NegativeInfinity,
                    double plain = 1.5) => name;
            }
            """);

        string source = GeneratorHarness.GeneratedSource(result);
        await Assert.That(source).Contains(": \"x\";");
        await Assert.That(source).Contains(": default;");
        await Assert.That(source).Contains(": true;");
        await Assert.That(source).Contains(": 7;");
        await Assert.That(source).Contains(": 9L;");
        await Assert.That(source).Contains(": double.NaN;");
        await Assert.That(source).Contains(": double.PositiveInfinity;");
        await Assert.That(source).Contains(": double.NegativeInfinity;");
        await Assert.That(source).Contains(": 1.5D;");
    }

    [Test]
    public async Task Enum_default_uses_member_name_when_representable()
    {
        var result = await GeneratorHarness.RunClean("""
            public enum Color { Red = 1, Green = 2 }

            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d")]
                public static string Paint(Color color = Color.Green) => "";
            }
            """);

        await Assert.That(GeneratorHarness.GeneratedSource(result)).Contains(": global::Color.Green;");
    }

    [Test]
    public async Task Enum_default_without_member_falls_back_to_cast()
    {
        var result = await GeneratorHarness.RunClean("""
            public enum Color { Red = 1, Green = 2 }

            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d")]
                public static string Paint(Color color = (Color)5) => "";
            }
            """);

        await Assert.That(GeneratorHarness.GeneratedSource(result)).Contains(": (global::Color)(5);");
    }

    [Test]
    public async Task Repeated_enum_parameters_share_one_parser()
    {
        var result = await GeneratorHarness.RunClean("""
            public enum Color { Red, Green }

            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d")]
                public static string Blend(Color first, Color second) => "";
            }
            """);

        string source = GeneratorHarness.GeneratedSource(result);
        int parserCount = source.Split(["static global::Color __EmissaryParse_Color"], StringSplitOptions.None).Length - 1;
        await Assert.That(parserCount).IsEqualTo(1);
    }

    [Test]
    public async Task Repeated_array_parameters_share_one_reader()
    {
        var result = await GeneratorHarness.RunClean("""
            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d")]
                public static string Combine(int[] first, int[] second) => "";
            }
            """);

        string source = GeneratorHarness.GeneratedSource(result);
        int readerCount = source.Split(["static int[] __EmissaryReadArray_Int"], StringSplitOptions.None).Length - 1;
        await Assert.That(readerCount).IsEqualTo(1);
    }

    [Test]
    public async Task Async_task_tool_with_all_primitive_kinds_and_cancellation_token()
    {
        var result = await GeneratorHarness.RunClean("""
            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d")]
                public static async System.Threading.Tasks.Task<string> Work(
                    bool flag, int count, long big, double ratio, string text,
                    System.Threading.CancellationToken token)
                {
                    await System.Threading.Tasks.Task.Yield();
                    return text;
                }
            }
            """);

        string source = GeneratorHarness.GeneratedSource(result);
        await Assert.That(source).Contains("await Work(__arg_flag, __arg_count, __arg_big, __arg_ratio, __arg_text, cancellationToken)");
        await Assert.That(source).Contains(".GetBoolean()");
        await Assert.That(source).Contains(".GetInt32()");
        await Assert.That(source).Contains(".GetInt64()");
        await Assert.That(source).Contains(".GetDouble()");
        await Assert.That(source).DoesNotContain("\"token\"");
    }

    [Test]
    public async Task Valuetask_tool_reads_all_array_element_kinds()
    {
        var result = await GeneratorHarness.RunClean("""
            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d")]
                public static System.Threading.Tasks.ValueTask<int> Count(
                    string[] names, bool[] flags, long[] bigs, double[] ratios) => new(1);
            }
            """);

        string source = GeneratorHarness.GeneratedSource(result);
        await Assert.That(source).Contains("__EmissaryReadArray_String");
        await Assert.That(source).Contains("__EmissaryReadArray_Bool");
        await Assert.That(source).Contains("__EmissaryReadArray_Long");
        await Assert.That(source).Contains("__EmissaryReadArray_Double");
        await Assert.That(source).Contains("boolean");
        await Assert.That(source).Contains("number");
    }

    [Test]
    public async Task All_return_kinds_emit_their_conversions()
    {
        var boolResult = await GeneratorHarness.RunClean("""
            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d")]
                public static bool Check(string text) => true;
            }
            """);
        await Assert.That(GeneratorHarness.GeneratedSource(boolResult))
            .Contains("__result ? \"true\" : \"false\"");

        var longResult = await GeneratorHarness.RunClean("""
            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d")]
                public static long Count(string text) => 1L;
            }
            """);
        await Assert.That(GeneratorHarness.GeneratedSource(longResult))
            .Contains("__result.ToString(global::System.Globalization.CultureInfo.InvariantCulture)");

        var doubleResult = await GeneratorHarness.RunClean("""
            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d")]
                public static double Measure(string text) => 1.5;
            }
            """);
        await Assert.That(GeneratorHarness.GeneratedSource(doubleResult))
            .Contains("__result.ToString(\"R\", global::System.Globalization.CultureInfo.InvariantCulture)");
    }

    [Test]
    public async Task Safety_attributes_flow_into_the_tool_definition()
    {
        var result = await GeneratorHarness.RunClean("""
            public static partial class Tools
            {
                [Emissary.AuthorizeTool("admin")]
                [Emissary.ClaudeTool(Description = "d", Untrusted = true, Privileged = true)]
                public static string Danger(string input) => input;
            }
            """);

        string source = GeneratorHarness.GeneratedSource(result);
        await Assert.That(source).Contains("\"admin\",");
        await Assert.That(source).Contains("true,");
        await Assert.That(source).Contains("null);");
    }

    [Test]
    public async Task Default_safety_metadata_is_emitted_explicitly()
    {
        var result = await GeneratorHarness.RunClean("""
            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d", Untrusted = false, Privileged = false)]
                public static string Safe(string input) => input;
            }
            """);

        string source = GeneratorHarness.GeneratedSource(result);
        await Assert.That(source).Contains("null,");
        await Assert.That(source).Contains("false,");
        await Assert.That(source).Contains("null);");
    }

    [Test]
    public async Task CompensatedBy_wires_the_compensator_dispatcher()
    {
        var result = await GeneratorHarness.RunClean("""
            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d", CompensatedBy = nameof(Cancel))]
                public static string Book(string room) => room;

                [Emissary.ClaudeTool(Description = "d")]
                public static string Cancel(string room) => room;
            }
            """);

        var bookSource = result.GeneratedSources.Single(s => s.HintName.Contains("Book")).SourceText.ToString();
        await Assert.That(bookSource).Contains("__EmissaryInvoke_Cancel,");
    }

    [Test]
    public async Task EMS009_when_compensator_is_missing()
    {
        var result = GeneratorHarness.Run("""
            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d", CompensatedBy = "Nope")]
                public static string Book(string room) => room;
            }
            """, out _);

        await Assert.That(GeneratorHarness.DiagnosticIds(result)).Contains("EMS009");
    }

    [Test]
    public async Task EMS009_when_compensator_is_not_a_tool()
    {
        var result = GeneratorHarness.Run("""
            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d", CompensatedBy = nameof(Cancel))]
                public static string Book(string room) => room;

                public static string Cancel(string room) => room;
            }
            """, out _);

        await Assert.That(GeneratorHarness.DiagnosticIds(result)).Contains("EMS009");
    }

    [Test]
    public async Task Enum_return_type_emits_to_string_conversion()
    {
        var result = await GeneratorHarness.RunClean("""
            public enum Color { Red, Green }

            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d")]
                public static Color Pick(string name) => Color.Red;
            }
            """);

        await Assert.That(GeneratorHarness.GeneratedSource(result)).Contains("__result.ToString()");
    }
}
