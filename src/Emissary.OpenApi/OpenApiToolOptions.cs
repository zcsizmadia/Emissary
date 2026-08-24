namespace Emissary.OpenApi;

/// <summary>
/// How a specification is turned into tools: which operations, under what names, and with what
/// safety posture.
/// </summary>
public sealed class OpenApiToolOptions
{
    /// <summary>
    /// Prepended to every generated tool name, so two specifications can be loaded side by side
    /// without colliding. <see langword="null"/> for no prefix.
    /// </summary>
    public string? Prefix { get; init; }

    /// <summary>
    /// The address requests are sent to. When <see langword="null"/>, the
    /// <see cref="HttpClient.BaseAddress"/> is used, and failing that the specification's first
    /// <c>servers</c> entry. Explicit configuration wins over the document, because the document
    /// names production and you may not mean production.
    /// </summary>
    public Uri? BaseAddress { get; init; }

    /// <summary>
    /// Include only operations carrying one of these tags. Empty means every tag.
    /// </summary>
    public IList<string> Tags { get; } = [];

    /// <summary>
    /// Include only operations with one of these operation ids. Empty means every operation.
    /// Combined with <see cref="Tags"/> by conjunction: both filters must pass.
    /// </summary>
    public IList<string> OperationIds { get; } = [];

    /// <summary>
    /// The most tools this specification may produce. Exceeding it throws, because a large public
    /// specification will happily generate four hundred tools and a prompt carrying four hundred
    /// tool schemas is its own failure — expensive, and worse at choosing. Filter by tag instead.
    /// </summary>
    public int MaxTools { get; init; } = 64;

    /// <summary>
    /// Caps how much of a response reaches the model, per
    /// <see cref="ToolDefinition.MaxResultLength"/>. An endpoint that returns a ten-megabyte page
    /// of results should not be able to end a run by itself.
    /// </summary>
    public int MaxResultLength { get; init; } = 16_000;

    /// <summary>
    /// Whether reads (<c>GET</c>, <c>HEAD</c>) are marked <see cref="ToolDefinition.Untrusted"/>,
    /// so their responses taint the run. On by default: a response body is content someone else
    /// wrote, and treating it as instructions is the whole prompt-injection problem.
    /// </summary>
    public bool ReadsAreUntrusted { get; init; } = true;

    /// <summary>
    /// Whether writes (<c>POST</c>, <c>PUT</c>, <c>PATCH</c>, <c>DELETE</c>) are marked
    /// <see cref="ToolDefinition.Privileged"/>, so they are blocked once the run is tainted. On by
    /// default, which combines with <see cref="ReadsAreUntrusted"/> into the useful invariant:
    /// having read from the API, the agent cannot then write to it without a human in between.
    /// </summary>
    public bool WritesArePrivileged { get; init; } = true;

    /// <summary>
    /// Policy an <see cref="IToolAuthorizer"/> must grant for a write operation, or
    /// <see langword="null"/> for none.
    /// </summary>
    public string? WritePolicy { get; init; }
}
