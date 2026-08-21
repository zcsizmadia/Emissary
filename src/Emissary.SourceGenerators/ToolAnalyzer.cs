using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Emissary.SourceGenerators;

/// <summary>Builds a <see cref="ToolModel"/> (or diagnostics) from a [ClaudeTool] method.</summary>
internal static class ToolAnalyzer
{
    public static ToolModel Analyze(GeneratorAttributeSyntaxContext context)
    {
        var method = (IMethodSymbol)context.TargetSymbol;
        var syntax = (MethodDeclarationSyntax)context.TargetNode;
        var location = syntax.Identifier.GetLocation();
        var model = new ToolModel { MethodName = method.Name };

        if (!method.IsStatic)
        {
            model.Diagnostics.Add(Diagnostic.Create(
                DiagnosticDescriptors.MethodNotStatic, location, method.Name));
        }

        bool isGeneric = method.IsGenericMethod;
        for (INamedTypeSymbol? type = method.ContainingType; type is not null; type = type.ContainingType)
        {
            if (type.TypeParameters.Length > 0)
            {
                isGeneric = true;
            }
        }

        if (isGeneric)
        {
            model.Diagnostics.Add(Diagnostic.Create(
                DiagnosticDescriptors.GenericsNotSupported, location, method.Name));
        }

        foreach (var typeDecl in syntax.Ancestors().OfType<TypeDeclarationSyntax>())
        {
            if (!typeDecl.Modifiers.Any(SyntaxKind.PartialKeyword))
            {
                model.Diagnostics.Add(Diagnostic.Create(
                    DiagnosticDescriptors.ContainingTypeNotPartial, location,
                    typeDecl.Identifier.ValueText, method.Name));
            }

            model.Containers.Insert(0, DeclarationKeyword(typeDecl) + " " + typeDecl.Identifier.ValueText);
        }

        model.Namespace = method.ContainingNamespace.IsGlobalNamespace
            ? null
            : method.ContainingNamespace.ToDisplayString();

        ReadAttribute(context.Attributes[0], method, location, model);
        AnalyzeParameters(method, location, model);
        AnalyzeReturn(method, location, model);

        model.HintName = NameHelpers.ToIdentifier(
            method.ContainingType.ToDisplayString() + "_" + method.Name) + ".g.cs";
        return model;
    }

    private static void ReadAttribute(AttributeData attribute, IMethodSymbol method, Location location, ToolModel model)
    {
        string? name = null;
        string? description = null;
        foreach (var argument in attribute.NamedArguments)
        {
            // ClaudeToolAttribute has exactly two settable properties.
            if (argument.Key == "Name")
            {
                name = argument.Value.Value as string;
            }
            else
            {
                description = argument.Value.Value as string;
            }
        }

        model.ToolName = string.IsNullOrWhiteSpace(name) ? NameHelpers.ToSnakeCase(method.Name) : name!;

        if (string.IsNullOrWhiteSpace(description))
        {
            model.Diagnostics.Add(Diagnostic.Create(
                DiagnosticDescriptors.MissingDescription, location, method.Name));
        }
        else
        {
            model.Description = description!;
        }
    }

    private static void AnalyzeParameters(IMethodSymbol method, Location location, ToolModel model)
    {
        foreach (var parameter in method.Parameters)
        {
            if (parameter.Type.ToDisplayString() == "System.Threading.CancellationToken")
            {
                model.Parameters.Add(new ParameterModel
                {
                    CSharpName = parameter.Name,
                    IsCancellationToken = true,
                });
                continue;
            }

            var parameterModel = new ParameterModel
            {
                CSharpName = parameter.Name,
                JsonName = NameHelpers.ToSnakeCase(parameter.Name),
                DeclaredTypeFullName = parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            };

            if (parameter.RefKind != RefKind.None || !TryMapKind(parameter.Type, parameterModel))
            {
                model.Diagnostics.Add(Diagnostic.Create(
                    DiagnosticDescriptors.UnsupportedParameterType, location,
                    parameter.Name, method.Name, parameter.Type.ToDisplayString()));
                continue;
            }

            if (parameter.HasExplicitDefaultValue)
            {
                parameterModel.IsOptional = true;
                parameterModel.DefaultLiteral = parameterModel.Kind == JsonKind.Enum
                    ? NameHelpers.FormatEnumDefault(parameterModel.EnumType!, parameter.ExplicitDefaultValue)
                    : NameHelpers.FormatDefault(parameter.ExplicitDefaultValue);
            }

            model.Parameters.Add(parameterModel);
        }
    }

    private static void AnalyzeReturn(IMethodSymbol method, Location location, ToolModel model)
    {
        ITypeSymbol returnType = method.ReturnType;
        if (returnType is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } named)
        {
            string constructed = named.ConstructedFrom.ToDisplayString();
            if (constructed is "System.Threading.Tasks.Task<TResult>" or "System.Threading.Tasks.ValueTask<TResult>")
            {
                model.IsAsync = true;
                returnType = named.TypeArguments[0];
            }
        }

        var probe = new ParameterModel();
        if (!TryMapKind(returnType, probe) || probe.Kind == JsonKind.Array)
        {
            model.Diagnostics.Add(Diagnostic.Create(
                DiagnosticDescriptors.UnsupportedReturnType, location,
                method.Name, method.ReturnType.ToDisplayString()));
            return;
        }

        model.ReturnKind = probe.Kind;
    }

    private static bool TryMapKind(ITypeSymbol type, ParameterModel model)
    {
        if (TryMapPrimitive(type, out JsonKind primitive))
        {
            model.Kind = primitive;
            return true;
        }

        if (type.TypeKind == TypeKind.Enum)
        {
            model.Kind = JsonKind.Enum;
            model.EnumType = (INamedTypeSymbol)type;
            return true;
        }

        if (type is IArrayTypeSymbol array && TryMapPrimitive(array.ElementType, out JsonKind element))
        {
            model.Kind = JsonKind.Array;
            model.ElementKind = element;
            return true;
        }

        return false;
    }

    private static bool TryMapPrimitive(ITypeSymbol type, out JsonKind kind)
    {
        switch (type.SpecialType)
        {
            case SpecialType.System_String:
                kind = JsonKind.String;
                return true;
            case SpecialType.System_Boolean:
                kind = JsonKind.Bool;
                return true;
            case SpecialType.System_Int32:
                kind = JsonKind.Int;
                return true;
            case SpecialType.System_Int64:
                kind = JsonKind.Long;
                return true;
            case SpecialType.System_Double:
                kind = JsonKind.Double;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    private static string DeclarationKeyword(TypeDeclarationSyntax typeDecl) =>
        typeDecl is RecordDeclarationSyntax { ClassOrStructKeyword.ValueText.Length: > 0 } record
            ? "record " + record.ClassOrStructKeyword.ValueText
            : typeDecl.Keyword.ValueText;
}
