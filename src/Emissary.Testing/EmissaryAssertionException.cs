namespace Emissary.Testing;

/// <summary>Thrown when an agent-run expectation fails. Recognized as a failure by every test framework.</summary>
public sealed class EmissaryAssertionException : Exception
{
    /// <summary>Creates the exception with a message describing the failed expectation.</summary>
    /// <param name="message">What was expected and what actually happened.</param>
    public EmissaryAssertionException(string message)
        : base(message)
    {
    }
}
