using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Emissary.SourceGenerators;

internal static class NameHelpers
{
    /// <summary>Converts PascalCase/camelCase to snake_case ("GetTemperature" → "get_temperature").</summary>
    public static string ToSnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>Makes a valid C# identifier fragment from a fully qualified type name.</summary>
    public static string ToIdentifier(string fullTypeName) =>
        fullTypeName.Replace('.', '_').Replace(':', '_');

    /// <summary>Formats a non-enum default parameter value as a C# literal.</summary>
    public static string FormatDefault(object? value) => value switch
    {
        null => "default",
        string s => SymbolDisplay.FormatLiteral(s, quote: true),
        bool b => b ? "true" : "false",
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture) + "L",
        double d when double.IsNaN(d) => "double.NaN",
        double d when double.IsPositiveInfinity(d) => "double.PositiveInfinity",
        double d when double.IsNegativeInfinity(d) => "double.NegativeInfinity",
        double d => d.ToString("R", CultureInfo.InvariantCulture) + "D",
        _ => ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture),
    };

    /// <summary>Formats an enum default parameter value as a C# literal, preferring the member name.</summary>
    public static string FormatEnumDefault(INamedTypeSymbol enumType, object? value)
    {
        string fullName = enumType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        foreach (var member in enumType.GetMembers())
        {
            if (member is IFieldSymbol { HasConstantValue: true } field && Equals(field.ConstantValue, value))
            {
                return fullName + "." + field.Name;
            }
        }

        return "(" + fullName + ")(" + ((IFormattable)value!).ToString(null, CultureInfo.InvariantCulture) + ")";
    }
}
