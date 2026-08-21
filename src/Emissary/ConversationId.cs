namespace Emissary;

/// <summary>
/// Strongly typed identifier for a single agent conversation.
/// </summary>
/// <remarks>
/// Ids are UUIDv7, so they sort by creation time — useful for state stores and trace correlation.
/// </remarks>
/// <param name="Value">The underlying globally unique value.</param>
public readonly record struct ConversationId(Guid Value)
{
    /// <summary>Creates a new, time-ordered conversation id.</summary>
    public static ConversationId New() => new(Guid.CreateVersion7());

    /// <summary>Parses a conversation id from its string form.</summary>
    /// <param name="text">Any format accepted by <see cref="Guid.Parse(string)"/>.</param>
    /// <exception cref="FormatException">The text is not a valid id.</exception>
    public static ConversationId Parse(string text) => new(Guid.Parse(text));

    /// <summary>Attempts to parse a conversation id from its string form.</summary>
    /// <param name="text">The candidate text, or <see langword="null"/>.</param>
    /// <param name="id">The parsed id, or <see langword="default"/> on failure.</param>
    /// <returns><see langword="true"/> if <paramref name="text"/> was a valid id.</returns>
    public static bool TryParse(string? text, out ConversationId id)
    {
        if (Guid.TryParse(text, out Guid value))
        {
            id = new(value);
            return true;
        }

        id = default;
        return false;
    }

    /// <summary>Returns the compact (32 hex digits, no hyphens) form of the id.</summary>
    public override string ToString() => Value.ToString("N");
}
