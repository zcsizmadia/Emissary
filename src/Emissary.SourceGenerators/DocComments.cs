using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Emissary.SourceGenerators;

/// <summary>Extracts tool and parameter descriptions from XML documentation comments.</summary>
internal static class DocComments
{
    public static (string? Summary, Dictionary<string, string> Parameters) Parse(string? xml)
    {
        var parameters = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(xml))
        {
            return (null, parameters);
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (XmlException)
        {
            // Roslyn hands back a plain "badly formed XML" comment for malformed doc comments.
            return (null, parameters);
        }

        XElement root = document.Root!;
        string? summary = Normalize(root.Element("summary")?.Value);
        foreach (var element in root.Elements("param"))
        {
            string? name = element.Attribute("name")?.Value;
            string? text = Normalize(element.Value);
            if (!string.IsNullOrEmpty(name) && text is not null)
            {
                parameters[name!] = text;
            }
        }

        return (summary, parameters);
    }

    /// <summary>Collapses a doc-comment block into a single trimmed line.</summary>
    public static string? Normalize(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var builder = new StringBuilder(text.Length);
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(trimmed);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    /// <summary>Escapes a string for embedding inside a JSON string literal.</summary>
    public static string JsonEscape(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        foreach (char c in value)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (c < ' ')
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.ToString();
    }
}
