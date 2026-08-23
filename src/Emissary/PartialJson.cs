using System.Text;
using System.Text.Json;

namespace Emissary;

/// <summary>
/// Turns a prefix of a streaming JSON document into the smallest valid JSON document that
/// represents what has arrived so far, so a partially received object can be deserialized while
/// the rest is still streaming.
/// </summary>
internal static class PartialJson
{
    /// <summary>
    /// Returns parseable JSON for <paramref name="prefix"/>, or <see langword="null"/> when not
    /// even a partial object can be recovered yet.
    /// </summary>
    /// <param name="prefix">The bytes of the document received so far.</param>
    public static string? TryComplete(string prefix)
    {
        var scan = Scan(prefix);
        if (scan.Containers.Count == 0)
        {
            // Either nothing has arrived yet, or the document is already complete.
            return IsParseable(prefix) ? prefix : null;
        }

        // First try keeping the trailing partial member, closing any open string and containers.
        string kept = prefix;
        if (scan.InString)
        {
            kept += '"';
        }

        if (Close(kept, scan.Containers) is { } completed && IsParseable(completed))
        {
            return completed;
        }

        // Otherwise drop the incomplete trailing member and close what remains. This is the case
        // for a half-written key ({"ti), a key with no value yet ({"title":), or a partial
        // literal ({"ok":tru).
        string trimmed = prefix[..scan.LastMemberBoundary].TrimEnd();
        if (trimmed.EndsWith(','))
        {
            trimmed = trimmed[..^1];
        }

        string? fallback = Close(trimmed, scan.Containers);
        return fallback is not null && IsParseable(fallback) ? fallback : null;
    }

    private static string? Close(string json, List<char> containers)
    {
        string trimmed = json.TrimEnd();

        // A dangling ':' or ',' cannot be closed into anything valid.
        if (trimmed.EndsWith(':') || trimmed.EndsWith(','))
        {
            return null;
        }

        var builder = new StringBuilder(trimmed);
        for (int i = containers.Count - 1; i >= 0; i--)
        {
            builder.Append(containers[i] == '{' ? '}' : ']');
        }

        return builder.ToString();
    }

    private static bool IsParseable(string json)
    {
        if (json.TrimStart().Length == 0)
        {
            return false;
        }

        try
        {
            using var _ = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ScanState Scan(string json)
    {
        var containers = new List<char>();
        bool inString = false;
        bool escaped = false;
        int lastMemberBoundary = 0;

        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                case '[':
                    containers.Add(c);
                    lastMemberBoundary = i + 1;
                    break;
                case '}':
                case ']':
                    if (containers.Count > 0)
                    {
                        containers.RemoveAt(containers.Count - 1);
                    }

                    break;
                case ',':
                    lastMemberBoundary = i + 1;
                    break;
                default:
                    break;
            }
        }

        return new ScanState(containers, inString, lastMemberBoundary);
    }

    private readonly record struct ScanState(List<char> Containers, bool InString, int LastMemberBoundary);
}
