using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Emissary.SourceGenerators;

/// <summary>The analysis result for one [ClaudeSchema] type.</summary>
internal sealed class SchemaModel
{
    public List<Diagnostic> Diagnostics { get; } = new();
    public List<PocoModel> Pocos { get; } = new();
    public List<string> Containers { get; } = new();
    public string? Namespace;
    public string TypeName = "";
    public string HintName = "";
    public string? RootDescription;
    public int RootIndex;

    public bool HasErrors => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
}

/// <summary>Builds a <see cref="SchemaModel"/> (or diagnostics) from a [ClaudeSchema] type.</summary>
internal static class SchemaAnalyzer
{
    public static SchemaModel Analyze(GeneratorAttributeSyntaxContext context)
    {
        var type = (INamedTypeSymbol)context.TargetSymbol;
        var syntax = (TypeDeclarationSyntax)context.TargetNode;
        var location = syntax.Identifier.GetLocation();
        var model = new SchemaModel { TypeName = type.Name };

        bool isGeneric = false;
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            if (current.TypeParameters.Length > 0)
            {
                isGeneric = true;
            }
        }

        if (isGeneric)
        {
            model.Diagnostics.Add(Diagnostic.Create(
                DiagnosticDescriptors.GenericsNotSupported, location, type.Name));
        }

        foreach (var typeDecl in syntax.AncestorsAndSelf().OfType<TypeDeclarationSyntax>())
        {
            if (!typeDecl.Modifiers.Any(SyntaxKind.PartialKeyword))
            {
                model.Diagnostics.Add(Diagnostic.Create(
                    DiagnosticDescriptors.ContainingTypeNotPartial, location,
                    typeDecl.Identifier.ValueText, type.Name));
            }

            model.Containers.Insert(0, ContainerSyntax.DeclarationKeyword(typeDecl) + " " + typeDecl.Identifier.ValueText);
        }

        model.Namespace = type.ContainingNamespace.IsGlobalNamespace
            ? null
            : type.ContainingNamespace.ToDisplayString();

        if (!model.HasErrors)
        {
            var mapper = new TypeMapper(model.Pocos, model.Diagnostics, location, type.Name);
            if (mapper.TryAnalyzePoco(type, out int rootIndex))
            {
                model.RootIndex = rootIndex;
                ApplyDocumentation(type, model);
            }
            else
            {
                model.Diagnostics.Add(Diagnostic.Create(
                    DiagnosticDescriptors.TypeNotSchemaRepresentable, location, type.Name));
            }
        }

        model.HintName = NameHelpers.ToIdentifier(type.ToDisplayString()) + "_Schema.g.cs";
        return model;
    }

    private static void ApplyDocumentation(INamedTypeSymbol type, SchemaModel model)
    {
        var (summary, parameterDocs) = DocComments.Parse(type.GetDocumentationCommentXml());
        model.RootDescription = summary;

        foreach (var member in model.Pocos[model.RootIndex].Members)
        {
            if (parameterDocs.TryGetValue(member.CSharpName, out string? doc))
            {
                member.Description = doc;
            }
        }
    }
}
