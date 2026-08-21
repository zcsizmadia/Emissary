using System.Collections.Immutable;

namespace Emissary;

/// <summary>Who authored a message.</summary>
public enum MessageRole
{
    /// <summary>The user (including tool results).</summary>
    User,

    /// <summary>The model.</summary>
    Assistant,
}

/// <summary>One immutable message in a conversation.</summary>
/// <param name="Role">Who authored the message.</param>
/// <param name="Content">The content blocks, in order.</param>
public sealed record Message(MessageRole Role, ImmutableArray<ContentBlock> Content)
{
    /// <summary>Creates a user message with a single text block.</summary>
    /// <param name="text">The user's text.</param>
    public static Message User(string text) => new(MessageRole.User, [new TextBlock(text)]);

    /// <summary>The concatenated text of all <see cref="TextBlock"/>s in this message.</summary>
    public string Text => string.Concat(Content.OfType<TextBlock>().Select(t => t.Text));
}
