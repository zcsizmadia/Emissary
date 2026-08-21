namespace Emissary.Testing;

/// <summary>Entry point for agent-run assertions.</summary>
public static class EmissaryAssert
{
    /// <summary>Starts a chain of expectations about an agent run.</summary>
    /// <param name="result">The run to assert on — live, recorded, or replayed.</param>
    public static AgentRunExpectations That(AgentResult result) => new(result);
}
