using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Emissary.SourceGenerators;

/// <summary>
/// Incremental source generator that turns <c>[ClaudeTool]</c>-annotated static methods into
/// <c>Emissary.ToolDefinition</c> properties — with a compile-time JSON Schema and a
/// reflection-free dispatcher, generated as <c>{MethodName}Tool</c> on the containing partial type.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ClaudeToolGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var tools = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Emissary.ClaudeToolAttribute",
            static (node, _) => node is MethodDeclarationSyntax,
            static (ctx, _) => ToolAnalyzer.Analyze(ctx));

        context.RegisterSourceOutput(tools, static (production, model) =>
        {
            foreach (var diagnostic in model.Diagnostics)
            {
                production.ReportDiagnostic(diagnostic);
            }

            if (!model.HasErrors)
            {
                production.AddSource(model.HintName, ToolEmitter.Emit(model));
            }
        });

        var schemas = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Emissary.ClaudeSchemaAttribute",
            static (node, _) => node is TypeDeclarationSyntax,
            static (ctx, _) => SchemaAnalyzer.Analyze(ctx));

        context.RegisterSourceOutput(schemas, static (production, model) =>
        {
            foreach (var diagnostic in model.Diagnostics)
            {
                production.ReportDiagnostic(diagnostic);
            }

            if (!model.HasErrors)
            {
                production.AddSource(model.HintName, SchemaEmitter.Emit(model));
            }
        });
    }
}
