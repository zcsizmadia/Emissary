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
    private readonly Dictionary<INamedTypeSymbol, int> _pocoIndex = new(SymbolEqualityComparer.Default);
    private readonly HashSet<INamedTypeSymbol> _visiting = new(SymbolEqualityComparer.Default);

    private ToolAnalyzer(GeneratorAttributeSyntaxContext context)
    {
        _method = (IMethodSymbol)context.TargetSymbol;
        _syntax = (MethodDeclarationSyntax)context.TargetNode;
        _attribute = context.Attributes[0];
        _location = _syntax.Identifier.GetLocation();
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

            _model.Containers.Insert(0, DeclarationKeyword(typeDecl) + " " + typeDecl.Identifier.ValueText);
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

            if (parameter.RefKind != RefKind.None || !TryMapKind(parameter.Type, parameterModel))
            {
                Report(DiagnosticDescriptors.UnsupportedParameterType,
                    parameter.Name, _method.Name, parameter.Type.ToDisplayString());
                continue;
            }

            if (parameter.HasExplicitDefaultValue)
            {
                parameterModel.IsOptional = true;
                parameterModel.DefaultLiteral = DefaultLiteral(parameterModel, parameter.ExplicitDefaultValue);
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

        var probe = new ParameterModel();
        if (!TryMapReturnKind(returnType, probe))
        {
            Report(DiagnosticDescriptors.UnsupportedReturnType, _method.Name, _method.ReturnType.ToDisplayString());
            return;
        }

        _model.ReturnKind = probe.Kind;
    }

    private static bool TryMapReturnKind(ITypeSymbol type, ParameterModel probe)
    {
        if (TryMapPrimitive(type, out JsonKind primitive))
        {
            probe.Kind = primitive;
            return true;
        }

        if (type.TypeKind == TypeKind.Enum)
        {
            probe.Kind = JsonKind.Enum;
            return true;
        }

        return false;
    }

    private bool TryMapKind(ITypeSymbol type, ParameterModel model)
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

        if (type is INamedTypeSymbol named
            && named.TypeKind is TypeKind.Class or TypeKind.Struct
            && named.SpecialType == SpecialType.None
            && TryAnalyzePoco(named, out int pocoIndex))
        {
            model.Kind = JsonKind.Object;
            model.PocoIndex = pocoIndex;
            return true;
        }

        return false;
    }

    private bool TryAnalyzePoco(INamedTypeSymbol type, out int index)
    {
        if (_pocoIndex.TryGetValue(type, out index))
        {
            return true;
        }

        index = -1;
        if (type.IsGenericType || type.IsAbstract || !_visiting.Add(type))
        {
            return false;
        }

        try
        {
            var poco = new PocoModel
            {
                FullName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                BinderName = "__EmissaryBind_" + NameHelpers.ToIdentifier(type.ToDisplayString()),
            };

            if (!TryCollectMembers(type, poco))
            {
                return false;
            }

            index = _model.Pocos.Count;
            _model.Pocos.Add(poco);
            _pocoIndex.Add(type, index);
            return true;
        }
        finally
        {
            _visiting.Remove(type);
        }
    }

    private bool TryCollectMembers(INamedTypeSymbol type, PocoModel poco)
    {
        var publicConstructors = type.InstanceConstructors
            .Where(c => c.DeclaredAccessibility == Accessibility.Public)
            .ToList();
        var parameterized = publicConstructors.Where(c => c.Parameters.Length > 0).ToList();

        if (parameterized.Count == 1)
        {
            poco.UsesConstructor = true;
            foreach (var parameter in parameterized[0].Parameters)
            {
                var member = CreateMember(type, parameter.Name, parameter.Type);
                if (member is null)
                {
                    return false;
                }

                if (parameter.HasExplicitDefaultValue)
                {
                    member.IsOptional = true;
                    member.DefaultLiteral = DefaultLiteral(member, parameter.ExplicitDefaultValue);
                }

                poco.Members.Add(member);
            }

            return true;
        }

        if (parameterized.Count > 1 || publicConstructors.Count == 0)
        {
            return false;
        }

        foreach (var property in type.GetMembers().OfType<IPropertySymbol>())
        {
            if (property.IsStatic
                || property.IsIndexer
                || property.DeclaredAccessibility != Accessibility.Public
                || property.SetMethod is not { DeclaredAccessibility: Accessibility.Public })
            {
                continue;
            }

            var member = CreateMember(type, property.Name, property.Type);
            if (member is null)
            {
                return false;
            }

            member.IsOptional = !property.IsRequired;
            member.DefaultLiteral = "default";
            poco.Members.Add(member);
        }

        return poco.Members.Count > 0;
    }

    private ParameterModel? CreateMember(INamedTypeSymbol owner, string memberName, ITypeSymbol memberType)
    {
        var member = new ParameterModel
        {
            CSharpName = memberName,
            JsonName = NameHelpers.ToSnakeCase(memberName),
            DeclaredTypeFullName = memberType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        };

        if (!TryMapKind(memberType, member))
        {
            Report(DiagnosticDescriptors.UnsupportedParameterType,
                owner.Name + "." + memberName, _method.Name, memberType.ToDisplayString());
            return null;
        }

        return member;
    }

    private static string DefaultLiteral(ParameterModel model, object? value) =>
        model.Kind == JsonKind.Enum
            ? NameHelpers.FormatEnumDefault(model.EnumType!, value)
            : NameHelpers.FormatDefault(value);

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

    private void Report(DiagnosticDescriptor descriptor, params object[] args) =>
        _model.Diagnostics.Add(Diagnostic.Create(descriptor, _location, args));
}
