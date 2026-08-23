using System.Globalization;

namespace Emissary;

/// <summary>Applies <see cref="ToolDefinition.MaxResultLength"/> to a tool's output.</summary>
internal static class ToolResultTruncation
{
    /// <summary>
    /// Trims <paramref name="content"/> to <paramref name="maxLength"/> characters and appends a
    /// plain-language notice, so the model can see that data was withheld rather than silently
    /// reasoning over a partial answer. The notice itself is not counted against the cap.
    /// </summary>
    /// <param name="content">The tool's output.</param>
    /// <param name="maxLength">The maximum number of characters to keep.</param>
    public static string Apply(string content, int maxLength)
    {
        if (content.Length <= maxLength)
        {
            return content;
        }

        int omitted = content.Length - maxLength;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{content[..maxLength]}\n[truncated: {omitted:N0} of {content.Length:N0} characters omitted — narrow the request if you need the rest]");
    }
}
