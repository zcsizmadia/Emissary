namespace Emissary;

/// <summary>
/// Thrown by generated tool dispatchers when the model supplies invalid tool input —
/// a required argument is missing, or a value cannot be converted to the parameter type.
/// </summary>
public sealed class ToolArgumentException : Exception
{
    /// <summary>Creates the exception with a message describing the invalid argument.</summary>
    /// <param name="message">What was wrong with the tool input.</param>
    public ToolArgumentException(string message)
        : base(message)
    {
    }
}
