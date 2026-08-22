using System.Collections.Immutable;
using System.Text.Json;
using Emissary.Serialization;

namespace Emissary;

/// <summary>The serializable shape of a <see cref="Conversation"/>.</summary>
/// <param name="Id">The conversation id.</param>
/// <param name="Messages">The messages, oldest first.</param>
public sealed record PersistedConversation(Guid Id, IReadOnlyList<Message> Messages);

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

    /// <summary>Serializes the conversation (id and messages) as JSON.</summary>
    public string ToJson() =>
        JsonSerializer.Serialize(new PersistedConversation(Id.Value, Messages), EmissaryJsonContext.Default.PersistedConversation);

    /// <summary>Deserializes a conversation previously produced by <see cref="ToJson"/>.</summary>
    /// <param name="json">The conversation JSON.</param>
    /// <exception cref="InvalidOperationException">The JSON is the null literal.</exception>
    public static Conversation FromJson(string json)
    {
        var persisted = JsonSerializer.Deserialize(json, EmissaryJsonContext.Default.PersistedConversation)
            ?? throw new InvalidOperationException("The conversation JSON deserialized to null.");
        return Restore(new ConversationId(persisted.Id), persisted.Messages);
    }
}
