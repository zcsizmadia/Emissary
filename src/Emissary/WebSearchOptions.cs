namespace Emissary;

/// <summary>
/// Enables Claude's server-side web search for a run. Unlike <c>[ClaudeTool]</c> tools, web
/// search executes on Anthropic's servers within a single turn — Emissary does not dispatch it.
/// </summary>
/// <remarks>
/// Because web search runs server-side, its results do not pass through Emissary's client tool
/// loop and are therefore <b>not</b> covered by taint tracking. Treat model output that used web
/// search as potentially influenced by untrusted web content.
/// </remarks>
public sealed class WebSearchOptions
{
    /// <summary>Maximum number of searches Claude may run in one turn; <see langword="null"/> for the model default.</summary>
    public int? MaxUses { get; set; }

    /// <summary>If non-empty, only these domains may be searched.</summary>
    public IList<string> AllowedDomains { get; } = [];

    /// <summary>Domains that must never be searched.</summary>
    public IList<string> BlockedDomains { get; } = [];
}
