using System.Collections.Immutable;

namespace Emissary;

/// <summary>
/// An immutable conversation: appending returns a new instance, so any point in an agent run
/// can be kept, compared, or replayed without defensive copies.
/// </summary>
public sealed class Conversation
{
    private Conversation(ConversationId id, ImmutableList<Message> messages)
    {
        Id = id;
        Messages = messages;
    }

    /// <summary>The stable identity of this conversation across appends.</summary>
    public ConversationId Id { get; }

    /// <summary>The messages, oldest first.</summary>
    public ImmutableList<Message> Messages { get; }

    /// <summary>Starts a new, empty conversation with a fresh id.</summary>
    public static Conversation Start() => new(ConversationId.New(), ImmutableList<Message>.Empty);

    /// <summary>Rebuilds a conversation from persisted state (e.g. a <see cref="SuspendedRun"/>).</summary>
    /// <param name="id">The original conversation id.</param>
    /// <param name="messages">The messages, oldest first.</param>
    public static Conversation Restore(ConversationId id, IEnumerable<Message> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return new(id, [.. messages]);
    }

    /// <summary>Returns a new conversation with <paramref name="message"/> appended.</summary>
    /// <param name="message">The message to append.</param>
    public Conversation Append(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new(Id, Messages.Add(message));
    }
}
