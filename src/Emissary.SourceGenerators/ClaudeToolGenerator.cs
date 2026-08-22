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

        // EMS010: [AuthorizeTool] on a method that is not a [ClaudeTool] is a silent no-op.
        var orphanedAuthorizations = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Emissary.AuthorizeToolAttribute",
            static (node, _) => node is MethodDeclarationSyntax,
            static (ctx, _) =>
            {
                var method = (IMethodSymbol)ctx.TargetSymbol;
                bool isTool = method.GetAttributes().Any(a =>
                    a.AttributeClass?.ToDisplayString() == "Emissary.ClaudeToolAttribute");
                return isTool
                    ? null
                    : Diagnostic.Create(
                        DiagnosticDescriptors.SafetyAttributeWithoutTool,
                        ctx.TargetNode.GetLocation(),
                        method.Name);
            });

        context.RegisterSourceOutput(orphanedAuthorizations, static (production, diagnostic) =>
        {
            if (diagnostic is not null)
            {
                production.ReportDiagnostic(diagnostic);
            }
        });
    }
}
