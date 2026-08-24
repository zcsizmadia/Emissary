using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Emissary.OpenApi;

/// <summary>Where a parameter goes on the wire.</summary>
internal enum ParameterLocation
{
    Path,
    Query,
}

/// <summary>One operation parameter, reduced to what sending a request needs.</summary>
internal sealed record OperationParameter(string Name, ParameterLocation Location, bool Required);

/// <summary>
/// Turns a tool call into an HTTP request. Holds only plain data and an absolute base address, so
/// the specification document is not kept alive past tool construction and the request does not
/// depend on how the caller's <see cref="HttpClient"/> happens to be configured.
/// </summary>
internal sealed class HttpOperationInvoker
{
    private readonly HttpClient _httpClient;
    private readonly Uri _baseAddress;
    private readonly HttpMethod _method;
    private readonly string _pathTemplate;
    private readonly IReadOnlyList<OperationParameter> _parameters;
    private readonly string? _bodyProperty;
    private readonly string _operation;

    public HttpOperationInvoker(
        HttpClient httpClient,
        Uri baseAddress,
        HttpMethod method,
        string pathTemplate,
        IReadOnlyList<OperationParameter> parameters,
        string? bodyProperty,
        string operation)
    {
        _httpClient = httpClient;
        _baseAddress = baseAddress;
        _method = method;
        _pathTemplate = pathTemplate;
        _parameters = parameters;
        _bodyProperty = bodyProperty;
        _operation = operation;
    }

    public async ValueTask<string> InvokeAsync(JsonElement input, CancellationToken cancellationToken)
    {
        using var request = BuildRequest(input);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        // A refusal is an answer. `404 no such customer` is information the model should act on, so
        // it is reported as content rather than raised as a tool failure; the status line is
        // included so the model can tell "not found" from "found nothing".
        return response.IsSuccessStatusCode && body.Length > 0
            ? body
            : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"
                + (body.Length > 0 ? $": {body}" : " (no body)");
    }

    private HttpRequestMessage BuildRequest(JsonElement input)
    {
        // Relative to the base address, which ends in a slash: a leading slash here would discard
        // the base address's own path, silently dropping the `/v1` out of `https://host/v1`.
        string path = _pathTemplate.TrimStart('/');
        var query = new StringBuilder();

        foreach (var parameter in _parameters)
        {
            if (!TryRead(input, parameter, out var value))
            {
                continue;
            }

            if (parameter.Location == ParameterLocation.Path)
            {
                path = path.Replace(
                    $"{{{parameter.Name}}}",
                    Uri.EscapeDataString(ToText(value)),
                    StringComparison.Ordinal);
            }
            else
            {
                AppendQuery(query, parameter.Name, value);
            }
        }

        if (query.Length > 0)
        {
            path = $"{path}?{query}";
        }

        var request = new HttpRequestMessage(_method, new Uri(_baseAddress, path));

        if (_bodyProperty is not null
            && input.ValueKind == JsonValueKind.Object
            && input.TryGetProperty(_bodyProperty, out var body))
        {
            request.Content = new StringContent(body.GetRawText(), Encoding.UTF8, "application/json");
        }

        return request;
    }

    /// <summary>Reads a parameter from the tool input, enforcing that required ones are present.</summary>
    private bool TryRead(JsonElement input, OperationParameter parameter, out JsonElement value)
    {
        if (input.ValueKind == JsonValueKind.Object
            && input.TryGetProperty(parameter.Name, out value)
            && value.ValueKind != JsonValueKind.Null)
        {
            return true;
        }

        if (parameter.Required)
        {
            throw new ToolArgumentException(
                $"Operation '{_operation}' is missing required parameter '{parameter.Name}'.");
        }

        value = default;
        return false;
    }

    /// <summary>Appends one query parameter, exploding an array into repeated entries.</summary>
    private static void AppendQuery(StringBuilder query, string name, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                Append(query, name, ToText(item));
            }

            return;
        }

        Append(query, name, ToText(value));

        static void Append(StringBuilder query, string name, string text)
        {
            if (query.Length > 0)
            {
                query.Append('&');
            }

            query.Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(text));
        }
    }

    /// <summary>
    /// Renders a JSON value the way a URL expects it: a string unquoted, everything else as written.
    /// </summary>
    private static string ToText(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
}
