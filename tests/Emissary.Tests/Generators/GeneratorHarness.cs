using System.Collections.Immutable;
using Emissary.SourceGenerators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Emissary.Tests.Generators;

internal static class GeneratorHarness
{
    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(CreateReferences);

    private static ImmutableArray<MetadataReference> CreateReferences()
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        paths.Add(typeof(ClaudeToolAttribute).Assembly.Location);
        return [.. paths.Select(MetadataReference (p) => MetadataReference.CreateFromFile(p))];
    }

    public static GeneratorRunResult Run(string source, out Compilation outputCompilation)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create(
            "GeneratorTests",
            [tree],
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new ClaudeToolGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out outputCompilation, out _);
        return driver.GetRunResult().Results[0];
    }

    /// <summary>Runs the generator and asserts the output compiles without errors.</summary>
    public static async Task<GeneratorRunResult> RunClean(string source)
    {
        var result = Run(source, out var output);
        var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        await Assert.That(errors).IsEmpty();
        return result;
    }

    public static IReadOnlyList<string> DiagnosticIds(GeneratorRunResult result) =>
        [.. result.Diagnostics.Select(d => d.Id)];

    public static string GeneratedSource(GeneratorRunResult result) =>
        result.GeneratedSources.Single().SourceText.ToString();
}
