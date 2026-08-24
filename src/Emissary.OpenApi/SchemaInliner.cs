using System.Text.Json;

namespace Emissary.OpenApi;

/// <summary>
/// Copies an OpenAPI schema into a tool's input schema, expanding <c>$ref</c> as it goes.
/// </summary>
/// <remarks>
/// Claude's tool schemas are self-contained, and a specification's are not: a real one keeps every
/// type in <c>components/schemas</c> and references it. So references are expanded in place rather
/// than passed through, and a reference cycle — which JSON Schema can express and a single inlined
/// document cannot — degrades to an open object instead of recursing forever.
/// </remarks>
internal static class SchemaInliner
{
    /// <summary>
    /// Keywords whose values are data rather than schemas. A <c>$ref</c>-looking key inside an
    /// example is an example, not a reference, so recursion stops here.
    /// </summary>
    private static readonly HashSet<string> DataKeywords = new(StringComparer.Ordinal)
    {
        "example", "examples", "default", "enum", "const",
    };

    /// <summary>Writes <paramref name="schema"/> with every reference expanded.</summary>
    /// <param name="writer">Where the schema is written.</param>
    /// <param name="schema">The schema to copy.</param>
    /// <param name="root">The specification document, for resolving references.</param>
    /// <param name="active">References currently being expanded, for cycle detection.</param>
    public static void Write(Utf8JsonWriter writer, JsonElement schema, JsonElement root, HashSet<string> active)
    {
        if (schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("$ref", out var reference)
            && reference.ValueKind == JsonValueKind.String)
        {
            WriteReference(writer, reference.GetString()!, root, active);
            return;
        }

        switch (schema.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in schema.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (DataKeywords.Contains(property.Name))
                    {
                        property.Value.WriteTo(writer);
                    }
                    else
                    {
                        Write(writer, property.Value, root, active);
                    }
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in schema.EnumerateArray())
                {
                    Write(writer, item, root, active);
                }

                writer.WriteEndArray();
                break;

            default:
                schema.WriteTo(writer);
                break;
        }
    }

    private static void WriteReference(Utf8JsonWriter writer, string pointer, JsonElement root, HashSet<string> active)
    {
        if (!active.Add(pointer))
        {
            // A type that contains itself. Nothing self-contained can describe it, so say what it
            // is and let the model send an object.
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WriteString("description", $"Recursive reference to {pointer}; shape repeats.");
            writer.WriteEndObject();
            return;
        }

        try
        {
            Write(writer, Resolve(pointer, root), root, active);
        }
        finally
        {
            active.Remove(pointer);
        }
    }

    /// <summary>
    /// Follows a single <c>$ref</c> if the element is one, and returns it unchanged otherwise.
    /// Specifications reference parameters and request bodies as well as schemas.
    /// </summary>
    /// <param name="element">The element, which may be a reference object.</param>
    /// <param name="root">The specification document.</param>
    public static JsonElement Resolve(JsonElement element, JsonElement root) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty("$ref", out var reference)
        && reference.ValueKind == JsonValueKind.String
            ? Resolve(reference.GetString()!, root)
            : element;

    /// <summary>Resolves a local JSON pointer such as <c>#/components/schemas/Pet</c>.</summary>
    private static JsonElement Resolve(string pointer, JsonElement root)
    {
        if (!pointer.StartsWith("#/", StringComparison.Ordinal))
        {
            throw new OpenApiSpecException(
                $"Reference '{pointer}' is not a local reference. Bundle the specification into a "
                + "single document first — remote references are not fetched, because reading a "
                + "specification should not make network calls.");
        }

        var current = root;
        foreach (string rawSegment in pointer[2..].Split('/'))
        {
            string segment = rawSegment.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                throw new OpenApiSpecException($"Reference '{pointer}' does not resolve.");
            }
        }

        return current;
    }
}
