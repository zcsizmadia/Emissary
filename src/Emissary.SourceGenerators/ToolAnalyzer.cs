using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Emissary.SourceGenerators;

/// <summary>Builds a <see cref="ToolModel"/> (or diagnostics) from a [ClaudeTool] method.</summary>
internal sealed class ToolAnalyzer
{
    private readonly IMethodSymbol _method;
    private readonly MethodDeclarationSyntax _syntax;
    private readonly AttributeData _attribute;
    private readonly Location _location;
    private readonly ToolModel _model = new();
    private readonly TypeMapper _mapper;

    private ToolAnalyzer(GeneratorAttributeSyntaxContext context)
    {
        _method = (IMethodSymbol)context.TargetSymbol;
        _syntax = (MethodDeclarationSyntax)context.TargetNode;
        _attribute = context.Attributes[0];
        _location = _syntax.Identifier.GetLocation();
        _mapper = new TypeMapper(_model.Pocos, _model.Diagnostics, _location, _method.Name);
    }

    public static ToolModel Analyze(GeneratorAttributeSyntaxContext context) =>
        new ToolAnalyzer(context).Run();

    private ToolModel Run()
    {
        _model.MethodName = _method.Name;

        if (!_method.IsStatic)
        {
            Report(DiagnosticDescriptors.MethodNotStatic, _method.Name);
        }

        bool isGeneric = _method.IsGenericMethod;
        for (INamedTypeSymbol? type = _method.ContainingType; type is not null; type = type.ContainingType)
        {
            if (type.TypeParameters.Length > 0)
            {
                isGeneric = true;
            }
        }

        if (isGeneric)
        {
            Report(DiagnosticDescriptors.GenericsNotSupported, _method.Name);
        }

        foreach (var typeDecl in _syntax.Ancestors().OfType<TypeDeclarationSyntax>())
        {
            if (!typeDecl.Modifiers.Any(SyntaxKind.PartialKeyword))
            {
                Report(DiagnosticDescriptors.ContainingTypeNotPartial, typeDecl.Identifier.ValueText, _method.Name);
            }

            _model.Containers.Insert(0, ContainerSyntax.DeclarationKeyword(typeDecl) + " " + typeDecl.Identifier.ValueText);
        }

        _model.Namespace = _method.ContainingNamespace.IsGlobalNamespace
            ? null
            : _method.ContainingNamespace.ToDisplayString();

        var (summary, parameterDocs) = DocComments.Parse(_method.GetDocumentationCommentXml());
        ReadAttribute(summary);
        AnalyzeParameters(parameterDocs);
        AnalyzeReturn();

        _model.HintName = NameHelpers.ToIdentifier(
            _method.ContainingType.ToDisplayString() + "_" + _method.Name) + ".g.cs";
        return _model;
    }

    private void ReadAttribute(string? docSummary)
    {
        string? name = null;
        string? description = null;
        foreach (var argument in _attribute.NamedArguments)
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

        _model.ToolName = string.IsNullOrWhiteSpace(name) ? NameHelpers.ToSnakeCase(_method.Name) : name!;

        if (!string.IsNullOrWhiteSpace(description))
        {
            _model.Description = description!;
        }
        else if (docSummary is not null)
        {
            _model.Description = docSummary;
        }
        else
        {
            Report(DiagnosticDescriptors.MissingDescription, _method.Name);
        }
    }

    private void AnalyzeParameters(Dictionary<string, string> parameterDocs)
    {
        foreach (var parameter in _method.Parameters)
        {
            if (parameter.Type.ToDisplayString() == "System.Threading.CancellationToken")
            {
                _model.Parameters.Add(new ParameterModel
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

            if (parameterDocs.TryGetValue(parameter.Name, out string? doc))
            {
                parameterModel.Description = doc;
            }

            if (parameter.RefKind != RefKind.None || !_mapper.TryMapKind(parameter.Type, parameterModel))
            {
                Report(DiagnosticDescriptors.UnsupportedParameterType,
                    parameter.Name, _method.Name, parameter.Type.ToDisplayString());
                continue;
            }

            if (parameter.HasExplicitDefaultValue)
            {
                parameterModel.IsOptional = true;
                parameterModel.DefaultLiteral = TypeMapper.DefaultLiteral(parameterModel, parameter.ExplicitDefaultValue);
            }

            _model.Parameters.Add(parameterModel);
        }
    }

    private void AnalyzeReturn()
    {
        ITypeSymbol returnType = _method.ReturnType;
        if (returnType is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } named)
        {
            string constructed = named.ConstructedFrom.ToDisplayString();
            if (constructed is "System.Threading.Tasks.Task<TResult>" or "System.Threading.Tasks.ValueTask<TResult>")
            {
                _model.IsAsync = true;
                returnType = named.TypeArguments[0];
            }
        }

        if (TypeMapper.TryMapPrimitive(returnType, out JsonKind primitive))
        {
            _model.ReturnKind = primitive;
            return;
        }

        if (returnType.TypeKind == TypeKind.Enum)
        {
            _model.ReturnKind = JsonKind.Enum;
            return;
        }

        Report(DiagnosticDescriptors.UnsupportedReturnType, _method.Name, _method.ReturnType.ToDisplayString());
    }

    private void Report(DiagnosticDescriptor descriptor, params object[] args) =>
        _model.Diagnostics.Add(Diagnostic.Create(descriptor, _location, args));
}
