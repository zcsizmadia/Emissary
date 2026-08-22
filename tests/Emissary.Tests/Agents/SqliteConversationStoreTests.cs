using Emissary.Sqlite;

namespace Emissary.Tests;

public sealed class SqliteConversationStoreTests
{
    [Test]
    public async Task Round_trips_a_conversation_through_a_database_file()
    {
        string path = Path.Combine(Path.GetTempPath(), $"emissary-conv-{Guid.CreateVersion7():N}.db");
        try
        {
            var store = new SqliteConversationStore($"Data Source={path}");
            var conversation = Conversation.Start()
                .Append(Message.User("hello"))
                .Append(new Message(MessageRole.Assistant, [new TextBlock("hi there")]));

            await store.SaveAsync(conversation);

            // A fresh instance over the same file still sees it (durability).
            var reopened = new SqliteConversationStore($"Data Source={path}");
            var loaded = await reopened.LoadAsync(conversation.Id);
            await Assert.That(loaded!.ToJson()).IsEqualTo(conversation.ToJson());

            await store.SaveAsync(conversation.Append(Message.User("again"))); // upsert
            await Assert.That((await store.LoadAsync(conversation.Id))!.Messages.Count).IsEqualTo(3);

            await store.DeleteAsync(conversation.Id);
            await Assert.That(await store.LoadAsync(conversation.Id)).IsNull();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Test]
    public async Task Validates_arguments()
    {
        await Assert.That(() => new SqliteConversationStore("")).Throws<ArgumentException>();
        var store = new SqliteConversationStore("Data Source=:memory:");
        await Assert.That(async () => { await store.SaveAsync(null!); }).Throws<ArgumentNullException>();
    }
}
