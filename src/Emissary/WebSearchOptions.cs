namespace Emissary;

/// <summary>
/// Enables Claude's server-side web search for a run. Unlike <c>[ClaudeTool]</c> tools, web
/// search executes on Anthropic's servers within a single turn — Emissary does not dispatch it.
/// </summary>
/// <remarks>
/// <para>
/// Because web search runs server-side, its results do not pass through Emissary's client tool
/// loop and are therefore <b>not</b> covered by taint tracking. Treat model output that used web
/// search as potentially influenced by untrusted web content.
/// </para>
/// <para>
/// <b>Known limitation.</b> Emissary assembles a turn's <c>text</c>, <c>thinking</c> and
/// <c>tool_use</c> blocks; the server-side blocks a search produces — <c>server_tool_use</c>,
/// <c>web_search_tool_result</c>, and the citations attached to text — are not yet modeled, so
/// they do not survive into the recorded conversation. Consequences: the model does not see its own
/// search results on a later turn (it may search again), citations are unavailable, and a turn
/// consisting only of server-side blocks ends the run (see <see cref="AgentStopReason.Paused"/>).
/// Single-turn search answers are unaffected. Round-tripping these blocks needs verification
/// against the live API, so it is deliberately not built on inference.
/// </para>
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
