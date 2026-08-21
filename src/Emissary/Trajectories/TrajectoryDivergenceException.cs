namespace Emissary;

/// <summary>
/// Thrown when a replayed agent run diverges from its trajectory — the agent made a different
/// request (or more requests) than were recorded.
/// </summary>
public sealed class TrajectoryDivergenceException : Exception
{
    /// <summary>Creates the exception with a message describing the divergence.</summary>
    /// <param name="message">How the run diverged from the recording.</param>
    public TrajectoryDivergenceException(string message)
        : base(message)
    {
    }
}
