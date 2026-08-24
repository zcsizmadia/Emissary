namespace Emissary.OpenApi;

/// <summary>
/// The specification could not be turned into tools: it is not readable as OpenAPI, it names no
/// address to send requests to, or it produces more tools than
/// <see cref="OpenApiToolOptions.MaxTools"/> allows.
/// </summary>
/// <remarks>
/// Always thrown while building the tool set, never during a run. A specification problem is a
/// startup problem, and finding it at startup is the point.
/// </remarks>
public sealed class OpenApiSpecException : Exception
{
    /// <summary>Creates the exception.</summary>
    public OpenApiSpecException()
        : base("The OpenAPI specification could not be read.")
    {
    }

    /// <summary>Creates the exception with a message describing the problem.</summary>
    /// <param name="message">What was wrong with the specification.</param>
    public OpenApiSpecException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the underlying failure.</summary>
    /// <param name="message">What was wrong with the specification.</param>
    /// <param name="innerException">The failure that revealed it.</param>
    public OpenApiSpecException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
