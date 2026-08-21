using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Emissary.SourceGenerators;

/// <summary>Builds JSON Schema strings from analyzed models — shared by tools and [ClaudeSchema] types.</summary>
internal static class SchemaJson
{
    /// <summary>Builds a root object schema.</summary>
    /// <param name="pocos">The poco table member models index into.</param>
    /// <param name="members">The root object's members.</param>
    /// <param name="strict">Emit "additionalProperties":false on every object level (structured outputs).</param>
    /// <param name="rootDescription">Optional description for the root object.</param>
    public static string BuildObject(
        IReadOnlyList<PocoModel> pocos,
        IEnumerable<ParameterModel> members,
        bool strict,
        string? rootDescription)
    {
        var builder = new StringBuilder(256);
        builder.Append('{');
        AppendObjectBody(builder, pocos, members, strict);
        if (rootDescription is not null)
        {
            builder.Append(",\"description\":\"").Append(DocComments.JsonEscape(rootDescription)).Append('"');
        }

        builder.Append('}');
        return builder.ToString();
    }

    private static void AppendObjectBody(
        StringBuilder builder,
        IReadOnlyList<PocoModel> pocos,
        IEnumerable<ParameterModel> members,
        bool strict)
    {
        builder.Append("\"type\":\"object\",\"properties\":{");

        bool first = true;
        var required = new List<string>();
        foreach (var member in members)
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;
            builder.Append('"').Append(member.JsonName).Append("\":");
            AppendTypeSchema(builder, pocos, member, strict);

            if (!member.IsOptional)
            {
                required.Add("\"" + member.JsonName + "\"");
            }
        }

        builder.Append('}');
        if (required.Count > 0)
        {
            builder.Append(",\"required\":[").Append(string.Join(",", required)).Append(']');
        }

        if (strict)
        {
            builder.Append(",\"additionalProperties\":false");
        }
    }

    private static void AppendTypeSchema(
        StringBuilder builder,
        IReadOnlyList<PocoModel> pocos,
        ParameterModel parameter,
        bool strict)
    {
        builder.Append('{');
        switch (parameter.Kind)
        {
            case JsonKind.Enum:
                builder.Append("\"type\":\"string\",\"enum\":[")
                    .Append(string.Join(",", EnumMemberNames(parameter.EnumType!).Select(n => "\"" + n + "\"")))
                    .Append(']');
                break;
            case JsonKind.Array:
                builder.Append("\"type\":\"array\",\"items\":{\"type\":\"")
                    .Append(SchemaTypeName(parameter.ElementKind)).Append("\"}");
                break;
            case JsonKind.Object:
                AppendObjectBody(builder, pocos, pocos[parameter.PocoIndex].Members, strict);
                break;
            default:
                builder.Append("\"type\":\"").Append(SchemaTypeName(parameter.Kind)).Append('"');
                break;
        }

        if (parameter.Description is not null)
        {
            builder.Append(",\"description\":\"").Append(DocComments.JsonEscape(parameter.Description)).Append('"');
        }

        builder.Append('}');
    }

    private static string SchemaTypeName(JsonKind kind) => kind switch
    {
        JsonKind.String => "string",
        JsonKind.Bool => "boolean",
        JsonKind.Int or JsonKind.Long => "integer",
        _ => "number",
    };

    public static IEnumerable<string> EnumMemberNames(INamedTypeSymbol enumType) =>
        enumType.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue).Select(f => f.Name);
}
