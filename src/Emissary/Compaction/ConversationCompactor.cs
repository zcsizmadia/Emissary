using System.Text;

namespace Emissary;

/// <summary>
/// The pure mechanics of client-side compaction: choosing a safe cut point, rendering the
/// older messages for summarization, and rebuilding the conversation around the summary.
/// </summary>
internal static class ConversationCompactor
{
    /// <summary>
    /// Finds the index at which to cut, or <see langword="null"/> when compaction isn't
    /// worthwhile. The kept suffix always starts at an assistant message, so an assistant
    /// tool-use turn is never separated from the user turn carrying its tool results, and roles
    /// still alternate after the summary is inserted as a user message.
    /// </summary>
    /// <param name="messages">The conversation, oldest first.</param>
    /// <param name="keepRecentMessages">The minimum number of trailing messages to preserve.</param>
    public static int? TryFindCutIndex(IReadOnlyList<Message> messages, int keepRecentMessages)
    {
        int earliestKeep = Math.Max(1, messages.Count - Math.Max(1, keepRecentMessages));
        for (int i = earliestKeep; i < messages.Count; i++)
        {
            if (messages[i].Role == MessageRole.Assistant)
            {
                // Everything before i is summarized; require at least two messages to be worth it.
                return i >= 2 ? i : null;
            }
        }

        return null;
    }

    /// <summary>Renders the messages before <paramref name="cutIndex"/> as a summarization prompt.</summary>
    /// <param name="messages">The conversation, oldest first.</param>
    /// <param name="cutIndex">The exclusive end of the range being summarized.</param>
    /// <param name="instruction">The summarization instruction.</param>
    public static string BuildSummaryPrompt(IReadOnlyList<Message> messages, int cutIndex, string instruction)
    {
        var builder = new StringBuilder(instruction).AppendLine().AppendLine().AppendLine("CONVERSATION:");
        for (int i = 0; i < cutIndex; i++)
        {
            builder.Append(messages[i].Role == MessageRole.User ? "User: " : "Assistant: ")
                .AppendLine(Render(messages[i]));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Rebuilds the conversation as a single summary user message followed by the preserved
    /// suffix, keeping the original conversation id.
    /// </summary>
    /// <param name="conversation">The conversation to compact.</param>
    /// <param name="cutIndex">The exclusive end of the summarized range.</param>
    /// <param name="summary">The summary text.</param>
    public static Conversation Apply(Conversation conversation, int cutIndex, string summary)
    {
        var messages = new List<Message>(conversation.Messages.Count - cutIndex + 1)
        {
            Message.User($"[Earlier conversation, summarized]\n{summary}"),
        };
        for (int i = cutIndex; i < conversation.Messages.Count; i++)
        {
            messages.Add(conversation.Messages[i]);
        }

        return Conversation.Restore(conversation.Id, messages);
    }

    private static string Render(Message message) =>
        string.Join(" ", message.Content.Select(block => block switch
        {
            TextBlock text => text.Text,
            ToolUseBlock toolUse => $"[called {toolUse.Name}]",
            ToolResultBlock result => $"[{(result.IsError ? "tool error" : "tool result")}: {result.Content}]",
            _ => string.Empty,
        }));
}
