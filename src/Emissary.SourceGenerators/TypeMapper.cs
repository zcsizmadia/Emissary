using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Emissary.SourceGenerators;

/// <summary>
/// Maps C# types onto <see cref="JsonKind"/>s and builds <see cref="PocoModel"/>s for
/// object types — shared by tool parameters and [ClaudeSchema] types.
/// </summary>
internal sealed class TypeMapper
{
    private readonly List<PocoModel> _pocos;
    private readonly List<Diagnostic> _diagnostics;
    private readonly Location _location;
    private readonly string _ownerName;
    private readonly Dictionary<INamedTypeSymbol, int> _pocoIndex = new(SymbolEqualityComparer.Default);
    private readonly HashSet<INamedTypeSymbol> _visiting = new(SymbolEqualityComparer.Default);

    public TypeMapper(List<PocoModel> pocos, List<Diagnostic> diagnostics, Location location, string ownerName)
    {
        _pocos = pocos;
        _diagnostics = diagnostics;
        _location = location;
        _ownerName = ownerName;
    }

    public bool TryMapKind(ITypeSymbol type, ParameterModel model)
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

    public bool TryAnalyzePoco(INamedTypeSymbol type, out int index)
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

            index = _pocos.Count;
            _pocos.Add(poco);
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
            _diagnostics.Add(Diagnostic.Create(
                DiagnosticDescriptors.UnsupportedParameterType, _location,
                owner.Name + "." + memberName, _ownerName, memberType.ToDisplayString()));
            return null;
        }

        return member;
    }

    public static string DefaultLiteral(ParameterModel model, object? value) =>
        model.Kind == JsonKind.Enum
            ? NameHelpers.FormatEnumDefault(model.EnumType!, value)
            : NameHelpers.FormatDefault(value);

    public static bool TryMapPrimitive(ITypeSymbol type, out JsonKind kind)
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
}
