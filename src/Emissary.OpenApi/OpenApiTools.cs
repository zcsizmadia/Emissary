using System.Buffers;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Emissary.OpenApi;

/// <summary>
/// Reads an OpenAPI specification and returns the tools it describes.
/// </summary>
/// <remarks>
/// <para>
/// The interesting part is not the schema translation, it is the safety posture that falls out of
/// the document for free. A specification already says which operations read and which ones write.
/// So reads become <see cref="ToolDefinition.Untrusted"/> and writes become
/// <see cref="ToolDefinition.Privileged"/>, and Emissary's existing taint tracking then enforces
/// the rule nobody wrote down: once the agent has read a response body — content someone else
/// authored — it can no longer write back through the same API. Point this at a stranger's
/// specification and the resulting agent is injection-safe by construction.
/// </para>
/// <para>
/// Authentication is the <see cref="HttpClient"/>'s job. Header parameters are never exposed to the
/// model, because a model that can set headers can set <c>Authorization</c>.
/// </para>
/// </remarks>
public static class OpenApiTools
{
    /// <summary>HTTP methods that become tools, and whether each one writes.</summary>
    private static readonly (string Key, bool Writes)[] SupportedMethods =
    [
        ("get", false),
        ("head", false),
        ("post", true),
        ("put", true),
        ("patch", true),
        ("delete", true),
    ];

    /// <summary>Reads the specification and builds one tool per selected operation.</summary>
    /// <param name="specJson">The specification, as JSON. (YAML is not read; convert it first.)</param>
    /// <param name="httpClient">The client requests are sent through, carrying any authentication.</param>
    /// <param name="options">Selection, naming and safety options; defaults when <see langword="null"/>.</param>
    /// <returns>The generated tools, and the operations that were skipped.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="OpenApiSpecException">
    /// The specification is unreadable, names no address, or produces more than
    /// <see cref="OpenApiToolOptions.MaxTools"/> tools.
    /// </exception>
    public static OpenApiToolSet FromSpec(
        string specJson,
        HttpClient httpClient,
        OpenApiToolOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(specJson);
        ArgumentNullException.ThrowIfNull(httpClient);
        options ??= new OpenApiToolOptions();

        using var document = Parse(specJson);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("paths", out var paths)
            || paths.ValueKind != JsonValueKind.Object)
        {
            throw new OpenApiSpecException(
                "The specification has no 'paths' object, so it describes no operations.");
        }

        var baseAddress = ResolveBaseAddress(root, httpClient, options);

        var tools = new List<ToolDefinition>();
        var skipped = new List<SkippedOperation>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in paths.EnumerateObject())
        {
            if (path.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var (methodKey, writes) in SupportedMethods)
            {
                if (!path.Value.TryGetProperty(methodKey, out var operation)
                    || operation.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                Build(
                    new OperationContext(path.Name, methodKey, writes, path.Value, operation, root),
                    baseAddress,
                    httpClient,
                    options,
                    names,
                    tools,
                    skipped);
            }
        }

        if (tools.Count > options.MaxTools)
        {
            throw new OpenApiSpecException(
                $"The specification produced {tools.Count} tools, over the limit of "
                + $"{options.MaxTools}. A prompt carrying that many tool schemas is expensive and "
                + "picks worse; narrow the set with OpenApiToolOptions.Tags or OperationIds, or "
                + "raise MaxTools deliberately.");
        }

        return new OpenApiToolSet(tools, skipped);
    }

    /// <summary>Everything about one operation that building a tool needs.</summary>
    private sealed record OperationContext(
        string Path,
        string MethodKey,
        bool Writes,
        JsonElement PathItem,
        JsonElement Operation,
        JsonElement Root)
    {
        public string Display => $"{MethodKey.ToUpperInvariant()} {Path}";
    }

    private static JsonDocument Parse(string specJson)
    {
        try
        {
            return JsonDocument.Parse(specJson);
        }
        catch (JsonException exception)
        {
            throw new OpenApiSpecException(
                "The specification is not valid JSON. YAML specifications must be converted first.",
                exception);
        }
    }

    /// <summary>
    /// Explicit configuration beats the document: the document names production, and you may not
    /// mean production.
    /// </summary>
    private static Uri ResolveBaseAddress(JsonElement root, HttpClient httpClient, OpenApiToolOptions options)
    {
        var address = options.BaseAddress ?? httpClient.BaseAddress ?? FirstServer(root);
        if (address is null)
        {
            throw new OpenApiSpecException(
                "No address to send requests to: the specification lists no 'servers', the "
                + "HttpClient has no BaseAddress, and OpenApiToolOptions.BaseAddress was not set.");
        }

        // Operation paths are appended relative to this, so it must end in a slash or its own path
        // segment is discarded.
        return address.AbsoluteUri.EndsWith('/') ? address : new Uri(address.AbsoluteUri + "/");
    }

    private static Uri? FirstServer(JsonElement root)
    {
        if (root.TryGetProperty("servers", out var servers) && servers.ValueKind == JsonValueKind.Array)
        {
            foreach (var server in servers.EnumerateArray())
            {
                if (server.ValueKind == JsonValueKind.Object
                    && Uri.TryCreate(Text(server, "url"), UriKind.Absolute, out var parsed))
                {
                    return parsed;
                }
            }
        }

        return null;
    }

    private static void Build(
        OperationContext context,
        Uri baseAddress,
        HttpClient httpClient,
        OpenApiToolOptions options,
        HashSet<string> names,
        List<ToolDefinition> tools,
        List<SkippedOperation> skipped)
    {
        string? operationId = Text(context.Operation, "operationId");
        if (options.OperationIds.Count > 0
            && (operationId is null || !options.OperationIds.Contains(operationId, StringComparer.Ordinal)))
        {
            return;
        }

        if (options.Tags.Count > 0 && !HasSelectedTag(context.Operation, options.Tags))
        {
            return;
        }

        var parameters = new List<OperationParameter>();
        var schemas = new List<(OperationParameter Parameter, JsonElement Schema, string? Description)>();
        foreach (var parameter in Parameters(context))
        {
            string? name = Text(parameter, "name");
            string? location = Text(parameter, "in");
            if (name is null || location is null || parameters.Any(p => p.Name == name))
            {
                continue;
            }

            bool required = Flag(parameter, "required");
            if (location is not ("path" or "query"))
            {
                // Headers and cookies are the client's business, not the model's. An operation that
                // cannot work without one is reported rather than generated half-working.
                if (required)
                {
                    skipped.Add(new SkippedOperation(
                        context.Display,
                        $"requires {location} parameter '{name}'; set it on the HttpClient instead."));
                    return;
                }

                continue;
            }

            // OpenAPI requires path parameters to be required; a specification that forgets to say
            // so would otherwise generate a URL with a literal '{id}' in it.
            var built = new OperationParameter(
                name,
                location == "path" ? ParameterLocation.Path : ParameterLocation.Query,
                required || location == "path");
            parameters.Add(built);
            schemas.Add((
                built,
                parameter.TryGetProperty("schema", out var schema) ? schema : default,
                Text(parameter, "description")));
        }

        JsonElement bodySchema = default;
        string? bodyProperty = null;
        string bodyMediaType = "application/json";
        bool bodyRequired = false;
        if (TryResolve(context.Operation, "requestBody", context.Root, out var requestBody))
        {
            if (!TryJsonContent(requestBody, out var content, out bodyMediaType))
            {
                skipped.Add(new SkippedOperation(
                    context.Display,
                    "its request body has no JSON media type."));
                return;
            }

            bodySchema = content.TryGetProperty("schema", out var schema) ? schema : default;
            bodyRequired = Flag(requestBody, "required");
            bodyProperty = parameters.Any(p => p.Name == "body") ? "request_body" : "body";
        }

        string toolName = UniqueName(
            (options.Prefix ?? "") + (operationId ?? $"{context.MethodKey}_{context.Path}"),
            names);

        var invoker = new HttpOperationInvoker(
            httpClient,
            baseAddress,
            new HttpMethod(context.MethodKey.ToUpperInvariant()),
            context.Path,
            parameters,
            bodyProperty,
            context.Display);

        tools.Add(new ToolDefinition(
            toolName,
            Describe(context),
            InputSchema(schemas, bodyProperty, bodySchema, bodyRequired, bodyMediaType, context.Root),
            invoker.InvokeAsync,
            requiredPolicy: context.Writes ? options.WritePolicy : null,
            untrusted: !context.Writes && options.ReadsAreUntrusted,
            privileged: context.Writes && options.WritesArePrivileged,
            maxResultLength: options.MaxResultLength));
    }

    /// <summary>Path-level parameters apply to every operation on the path, then the operation's own.</summary>
    private static IEnumerable<JsonElement> Parameters(OperationContext context)
    {
        foreach (var source in new[] { context.Operation, context.PathItem })
        {
            if (source.TryGetProperty("parameters", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var parameter in list.EnumerateArray())
                {
                    yield return SchemaInliner.Resolve(parameter, context.Root);
                }
            }
        }
    }

    private static string Describe(OperationContext context)
    {
        string? summary = Text(context.Operation, "summary") ?? Text(context.Operation, "description");
        return summary is null ? context.Display : $"{summary} ({context.Display})";
    }

    /// <summary>Assembles the tool's input schema from the parameters and the request body.</summary>
    private static string InputSchema(
        List<(OperationParameter Parameter, JsonElement Schema, string? Description)> parameters,
        string? bodyProperty,
        JsonElement bodySchema,
        bool bodyRequired,
        string bodyMediaType,
        JsonElement root)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WriteStartObject("properties");

            foreach (var (parameter, schema, description) in parameters)
            {
                writer.WritePropertyName(parameter.Name);
                WriteSchema(writer, schema, description, "string", root);
            }

            if (bodyProperty is not null)
            {
                writer.WritePropertyName(bodyProperty);
                WriteSchema(writer, bodySchema, $"Request body ({bodyMediaType}).", "object", root);
            }

            writer.WriteEndObject();

            writer.WriteStartArray("required");
            foreach (var (parameter, _, _) in parameters.Where(p => p.Parameter.Required))
            {
                writer.WriteStringValue(parameter.Name);
            }

            if (bodyProperty is not null && bodyRequired)
            {
                writer.WriteStringValue(bodyProperty);
            }

            writer.WriteEndArray();

            // Closed by default: an invented parameter is a mistake worth surfacing as one.
            writer.WriteBoolean("additionalProperties", false);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Writes one property's schema with references expanded, carrying the specification's
    /// description across when the schema itself does not have one — a specification usually
    /// documents the parameter, not its type, and the model reads the tool schema.
    /// </summary>
    private static void WriteSchema(
        Utf8JsonWriter writer,
        JsonElement schema,
        string? description,
        string fallbackType,
        JsonElement root)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var inliner = new Utf8JsonWriter(buffer))
        {
            if (schema.ValueKind == JsonValueKind.Undefined)
            {
                // Nothing said what shape this is, so allow the shape it probably is.
                inliner.WriteStartObject();
                inliner.WriteString("type", fallbackType);
                inliner.WriteEndObject();
            }
            else
            {
                SchemaInliner.Write(inliner, schema, root, new HashSet<string>(StringComparer.Ordinal));
            }
        }

        using var inlined = JsonDocument.Parse(buffer.WrittenMemory);
        var element = inlined.RootElement;
        if (description is null
            || element.ValueKind != JsonValueKind.Object
            || element.TryGetProperty("description", out _))
        {
            element.WriteTo(writer);
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("description", description);
        foreach (var property in element.EnumerateObject())
        {
            writer.WritePropertyName(property.Name);
            property.Value.WriteTo(writer);
        }

        writer.WriteEndObject();
    }

    /// <summary>Finds a JSON media type in a request body's content map.</summary>
    private static bool TryJsonContent(JsonElement requestBody, out JsonElement content, out string mediaType)
    {
        if (requestBody.TryGetProperty("content", out var map) && map.ValueKind == JsonValueKind.Object)
        {
            foreach (var candidate in map.EnumerateObject())
            {
                if (candidate.Name.Equals("application/json", StringComparison.OrdinalIgnoreCase)
                    || candidate.Name.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
                {
                    content = candidate.Value;
                    mediaType = candidate.Name;
                    return true;
                }
            }
        }

        content = default;
        mediaType = "application/json";
        return false;
    }

    private static bool HasSelectedTag(JsonElement operation, IList<string> selected)
    {
        if (operation.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
        {
            foreach (var tag in tags.EnumerateArray())
            {
                if (tag.ValueKind == JsonValueKind.String
                    && selected.Contains(tag.GetString()!, StringComparer.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryResolve(JsonElement owner, string property, JsonElement root, out JsonElement resolved)
    {
        if (owner.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object)
        {
            resolved = SchemaInliner.Resolve(value, root);
            return true;
        }

        resolved = default;
        return false;
    }

    private static string? Text(JsonElement owner, string property) =>
        owner.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Flag(JsonElement owner, string property) =>
        owner.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Sanitizes a name to what the API accepts (letters, digits, underscore, hyphen; 64 characters)
    /// and disambiguates a collision, which a specification without operation ids will produce.
    /// </summary>
    private static string UniqueName(string candidate, HashSet<string> taken)
    {
        var text = new StringBuilder(candidate.Length);
        foreach (char character in candidate)
        {
            text.Append(char.IsAsciiLetterOrDigit(character) || character is '_' or '-' ? character : '_');
        }

        string name = text.ToString();
        if (name.Length > 64)
        {
            name = name[..64];
        }

        string unique = name;
        for (int suffix = 2; !taken.Add(unique); suffix++)
        {
            string tail = suffix.ToString(CultureInfo.InvariantCulture);
            unique = name.Length + tail.Length + 1 > 64
                ? $"{name[..(63 - tail.Length)]}_{tail}"
                : $"{name}_{tail}";
        }

        return unique;
    }
}
