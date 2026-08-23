using Emissary.Tests.Generators;

namespace Emissary.Tests;

public sealed class GeneratorDiagnosticsTests
{
    [Test]
    public async Task EMS001_missing_description_warns_but_still_emits()
    {
        var result = GeneratorHarness.Run("""
            public static partial class Tools
            {
                [Emissary.ClaudeTool]
                public static string Echo(string text) => text;
            }
            """, out _);

        await Assert.That(GeneratorHarness.DiagnosticIds(result)).Contains("EMS001");
        await Assert.That(result.GeneratedSources.Length).IsEqualTo(1);
        await Assert.That(GeneratorHarness.GeneratedSource(result)).Contains("\"\"");
    }

    [Test]
    public async Task EMS003_parameter_without_description_is_an_info()
    {
        var result = GeneratorHarness.Run("""
            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d")]
                public static string Echo(string text) => text;
            }
            """, out _);

        var ems003 = result.Diagnostics.Single(d => d.Id == "EMS003");
        await Assert.That(ems003.Severity).IsEqualTo(Microsoft.CodeAnalysis.DiagnosticSeverity.Info);
        await Assert.That(ems003.GetMessage(null)).Contains("'text'");
    }

    [Test]
    public async Task EMS003_not_raised_when_parameters_are_documented()
    {
        var result = GeneratorHarness.Run("""
            public static partial class Tools
            {
                /// <summary>d</summary>
                /// <param name="text">The text.</param>
                [Emissary.ClaudeTool]
                public static string Echo(string text) => text;
            }
            """, out _);

        await Assert.That(GeneratorHarness.DiagnosticIds(result)).DoesNotContain("EMS003");
    }

    [Test]
    public async Task EMS011_negative_max_result_length_is_an_error()
    {
        var result = GeneratorHarness.Run("""
            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d", MaxResultLength = -5)]
                public static string Dump(string table) => table;
            }
            """, out _);

        var ems011 = result.Diagnostics.Single(d => d.Id == "EMS011");
        await Assert.That(ems011.GetMessage(null)).Contains("-5");
    }

    [Test]
    public async Task Max_result_length_flows_into_the_generated_tool()
    {
        var result = await GeneratorHarness.RunClean("""
            public static partial class Tools
            {
                /// <summary>d</summary>
                /// <param name="table">The table.</param>
                [Emissary.ClaudeTool(MaxResultLength = 4096)]
                public static string Dump(string table) => table;
            }
            """);

        await Assert.That(GeneratorHarness.GeneratedSource(result)).Contains("4096);");
    }

    [Test]
    public async Task Zero_max_result_length_means_no_cap()
    {
        var result = await GeneratorHarness.RunClean("""
            public static partial class Tools
            {
                /// <summary>d</summary>
                /// <param name="table">The table.</param>
                [Emissary.ClaudeTool(MaxResultLength = 0)]
                public static string Dump(string table) => table;
            }
            """);

        await Assert.That(GeneratorHarness.GeneratedSource(result)).Contains("null);");
    }

    [Test]
    public async Task EMS010_authorize_without_tool_is_a_warning()
    {
        var result = GeneratorHarness.Run("""
            public static partial class Tools
            {
                [Emissary.AuthorizeTool("admin")]
                public static string Orphan(string id) => id;
            }
            """, out _);

        var ems010 = result.Diagnostics.Single(d => d.Id == "EMS010");
        await Assert.That(ems010.Severity).IsEqualTo(Microsoft.CodeAnalysis.DiagnosticSeverity.Warning);
        await Assert.That(ems010.GetMessage(null)).Contains("Orphan");
    }

    [Test]
    public async Task EMS010_not_raised_when_authorize_is_paired_with_tool()
    {
        var result = GeneratorHarness.Run("""
            public static partial class Tools
            {
                [Emissary.AuthorizeTool("admin")]
                [Emissary.ClaudeTool(Description = "d")]
                public static string DeleteData(string id) => id;
            }
            """, out _);

        await Assert.That(GeneratorHarness.DiagnosticIds(result)).DoesNotContain("EMS010");
    }

    [Test]
    public async Task EMS002_unsupported_parameter_type_is_an_error()
    {
        var result = GeneratorHarness.Run("""
            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d")]
                public static string Bad(System.DateTime when) => "";
            }
            """, out _);

        await Assert.That(GeneratorHarness.DiagnosticIds(result)).Contains("EMS002");
        await Assert.That(result.GeneratedSources.Length).IsEqualTo(0);
    }

    [Test]
    public async Task EMS002_ref_parameter_is_an_error()
    {
        var result = GeneratorHarness.Run("""
            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d")]
                public static string Bad(ref int value) => "";
            }
            """, out _);

        await Assert.That(GeneratorHarness.DiagnosticIds(result)).Contains("EMS002");
    }

    [Test]
    public async Task EMS002_array_of_unsupported_element_is_an_error()
    {
        var result = GeneratorHarness.Run("""
            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d")]
                public static string Bad(System.DateTime[] whens) => "";
            }
            """, out _);

        await Assert.That(GeneratorHarness.DiagnosticIds(result)).Contains("EMS002");
    }

    [Test]
    public async Task EMS004_non_partial_containing_type_is_an_error()
    {
        var result = GeneratorHarness.Run("""
            public static class Tools
            {
                [Emissary.ClaudeTool(Description = "d")]
                public static string Echo(string text) => text;
            }
            """, out _);

        await Assert.That(GeneratorHarness.DiagnosticIds(result)).Contains("EMS004");
    }

    [Test]
    public async Task EMS004_reports_non_partial_outer_type_of_nested_tool()
    {
        var result = GeneratorHarness.Run("""
            public static class Outer
            {
                public static partial class Inner
                {
                    [Emissary.ClaudeTool(Description = "d")]
                    public static string Echo(string text) => text;
                }
            }
            """, out _);

        var diagnostic = result.Diagnostics.Single(d => d.Id == "EMS004");
        await Assert.That(diagnostic.GetMessage(null)).Contains("'Outer'");
    }

    [Test]
    public async Task EMS012_compensator_must_match_the_tools_static_ness()
    {
        var result = GeneratorHarness.Run("""
            public partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d", CompensatedBy = nameof(Undo))]
                public string Book(string room) => room;

                [Emissary.ClaudeTool(Description = "d")]
                public static string Undo(string room) => room;
            }
            """, out _);

        var diagnostic = result.Diagnostics.Single(d => d.Id == "EMS012");
        await Assert.That(diagnostic.GetMessage(null)).Contains("'Undo', which is static");
    }

    [Test]
    public async Task EMS012_also_catches_a_static_tool_with_an_instance_compensator()
    {
        var result = GeneratorHarness.Run("""
            public partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d", CompensatedBy = nameof(Undo))]
                public static string Book(string room) => room;

                [Emissary.ClaudeTool(Description = "d")]
                public string Undo(string room) => room;
            }
            """, out _);

        var diagnostic = result.Diagnostics.Single(d => d.Id == "EMS012");
        await Assert.That(diagnostic.GetMessage(null)).Contains("which is an instance method");
    }

    [Test]
    public async Task EMS006_void_return_is_an_error()
    {
        var result = GeneratorHarness.Run("""
            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d")]
                public static void Fire(string target) { _ = target; }
            }
            """, out _);

        await Assert.That(GeneratorHarness.DiagnosticIds(result)).Contains("EMS006");
    }

    [Test]
    public async Task EMS006_array_return_is_an_error()
    {
        var result = GeneratorHarness.Run("""
            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d")]
                public static int[] Numbers() => [];
            }
            """, out _);

        await Assert.That(GeneratorHarness.DiagnosticIds(result)).Contains("EMS006");
    }

    [Test]
    public async Task EMS006_generic_non_awaitable_return_is_an_error()
    {
        var result = GeneratorHarness.Run("""
            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d")]
                public static System.Collections.Generic.List<string> Names() => [];
            }
            """, out _);

        await Assert.That(GeneratorHarness.DiagnosticIds(result)).Contains("EMS006");
    }

    [Test]
    public async Task EMS006_non_generic_task_return_is_an_error()
    {
        var result = GeneratorHarness.Run("""
            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d")]
                public static System.Threading.Tasks.Task Fire() => System.Threading.Tasks.Task.CompletedTask;
            }
            """, out _);

        await Assert.That(GeneratorHarness.DiagnosticIds(result)).Contains("EMS006");
    }

    [Test]
    public async Task EMS007_generic_method_is_an_error()
    {
        var result = GeneratorHarness.Run("""
            public static partial class Tools
            {
                [Emissary.ClaudeTool(Description = "d")]
                public static string Echo<T>(string text) => text;
            }
            """, out _);

        await Assert.That(GeneratorHarness.DiagnosticIds(result)).Contains("EMS007");
    }

    [Test]
    public async Task EMS007_generic_containing_type_is_an_error()
    {
        var result = GeneratorHarness.Run("""
            public static partial class Tools<T>
            {
                [Emissary.ClaudeTool(Description = "d")]
                public static string Echo(string text) => text;
            }
            """, out _);

        await Assert.That(GeneratorHarness.DiagnosticIds(result)).Contains("EMS007");
    }

    [Test]
    public async Task Attribute_on_non_method_node_is_ignored()
    {
        var result = GeneratorHarness.Run("""
            [Emissary.ClaudeTool(Description = "d")]
            public partial class NotATool
            {
            }
            """, out _);

        await Assert.That(result.GeneratedSources.Length).IsEqualTo(0);
        await Assert.That(result.Diagnostics.Length).IsEqualTo(0);
    }
}
