using Emissary.Tests.Generators;

namespace Emissary.Tests;

public sealed class GeneratorSchemaTests
{
    [Test]
    public async Task Global_namespace_record_gets_strict_schema_property()
    {
        var result = await GeneratorHarness.RunClean("""
            [Emissary.ClaudeSchema]
            public sealed partial record Answer(string Text, int Confidence = 0);
            """);

        string source = GeneratorHarness.GeneratedSource(result);
        await Assert.That(source).Contains("public static string JsonSchema { get; }");
        await Assert.That(source).Contains("additionalProperties");
        await Assert.That(source).DoesNotContain("namespace");
    }

    [Test]
    public async Task Nested_namespaced_type_reproduces_containers()
    {
        var result = await GeneratorHarness.RunClean("""
            namespace Deep;

            public partial struct Outer
            {
                /// <summary>An inner thing.</summary>
                /// <param name="Name">Its name.</param>
                [Emissary.ClaudeSchema]
                public sealed partial record Inner(string Name);
            }
            """);

        string source = GeneratorHarness.GeneratedSource(result);
        await Assert.That(source).Contains("namespace Deep");
        await Assert.That(source).Contains("partial struct Outer");
        await Assert.That(source).Contains("partial record Inner");
        await Assert.That(source).Contains("An inner thing.");
        await Assert.That(source).Contains("Its name.");
    }

    [Test]
    public async Task Property_mode_schema_type_honors_required()
    {
        var result = await GeneratorHarness.RunClean("""
            [Emissary.ClaudeSchema]
            public sealed partial class Settings
            {
                public required string Theme { get; set; }
                public int Size { get; init; }
            }
            """);

        string source = GeneratorHarness.GeneratedSource(result);
        await Assert.That(source).Contains("\\\"required\\\":[\\\"theme\\\"]");
    }

    [Test]
    public async Task Non_partial_schema_type_is_an_error()
    {
        var result = GeneratorHarness.Run("""
            [Emissary.ClaudeSchema]
            public sealed record Answer(string Text);
            """, out _);

        await Assert.That(GeneratorHarness.DiagnosticIds(result)).Contains("EMS004");
        await Assert.That(GeneratorHarness.DiagnosticIds(result)).DoesNotContain("EMS008");
        await Assert.That(result.GeneratedSources.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Generic_schema_type_is_an_error()
    {
        var result = GeneratorHarness.Run("""
            [Emissary.ClaudeSchema]
            public sealed partial record Box<T>(T Value);
            """, out _);

        await Assert.That(GeneratorHarness.DiagnosticIds(result)).Contains("EMS007");
    }

    [Test]
    public async Task Schema_type_nested_in_generic_container_is_an_error()
    {
        var result = GeneratorHarness.Run("""
            public partial class Container<T>
            {
                [Emissary.ClaudeSchema]
                public sealed partial record Inner(string Name);
            }
            """, out _);

        await Assert.That(GeneratorHarness.DiagnosticIds(result)).Contains("EMS007");
    }

    [Test]
    public async Task Unrepresentable_schema_type_is_an_error()
    {
        var result = GeneratorHarness.Run("""
            [Emissary.ClaudeSchema]
            public sealed partial class Hidden
            {
                private Hidden() { }
            }
            """, out _);

        await Assert.That(GeneratorHarness.DiagnosticIds(result)).Contains("EMS008");
    }

    [Test]
    public async Task Schema_type_without_docs_has_no_descriptions()
    {
        var result = await GeneratorHarness.RunClean("""
            [Emissary.ClaudeSchema]
            public sealed partial record Plain(string Name);
            """);

        await Assert.That(GeneratorHarness.GeneratedSource(result)).DoesNotContain("description");
    }

    [Test]
    public async Task Attribute_on_enum_is_ignored()
    {
        var result = GeneratorHarness.Run("""
            [Emissary.ClaudeSchema]
            public enum Sizes { Small, Large }
            """, out _);

        await Assert.That(result.GeneratedSources.Length).IsEqualTo(0);
        await Assert.That(result.Diagnostics.Length).IsEqualTo(0);
    }
}
