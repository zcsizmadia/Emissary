using System.Text.Json;
using Emissary.Sqlite;

namespace Emissary.Tests;

public sealed class SqliteStoreTests
{
    private static SuspendedRun CreateRun()
    {
        using var document = JsonDocument.Parse("""{"amount":5}""");
        return new SuspendedRun(
            Guid.CreateVersion7(),
            [Message.User("pay"),
             new Message(MessageRole.Assistant, [new ToolUseBlock("t1", "send_payment", document.RootElement.Clone())])],
            new AgentUsage(10, 5),
            [],
            [new PlannedEffect("send_payment", "t1", document.RootElement.Clone())],
            new GuardSnapshot(["lookup_order"], new Dictionary<string, int> { ["send_payment"] = 1 }, null, false, null),
            []);
    }

    [Test]
    public async Task Save_load_delete_round_trip_through_a_database_file()
    {
        string path = Path.Combine(Path.GetTempPath(), $"emissary-{Guid.CreateVersion7():N}.db");
        try
        {
            var store = new SqliteAgentStateStore($"Data Source={path}");
            var run = CreateRun();

            await store.SaveAsync(run);
            var loaded = await store.LoadAsync(run.ConversationId);

            await Assert.That(loaded!.ToJson()).IsEqualTo(run.ToJson());
            await Assert.That(loaded.Guard.Succeeded.Single()).IsEqualTo("lookup_order");

            // Overwrite is an upsert.
            await store.SaveAsync(run);

            // Durability: a fresh store instance over the same file still sees it.
            var reopened = new SqliteAgentStateStore($"Data Source={path}");
            await Assert.That((await reopened.LoadAsync(run.ConversationId))).IsNotNull();

            await store.DeleteAsync(run.ConversationId);
            await Assert.That(await store.LoadAsync(run.ConversationId)).IsNull();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Test]
    public async Task Store_validates_arguments()
    {
        await Assert.That(() => new SqliteAgentStateStore("")).Throws<ArgumentException>();
        var store = new SqliteAgentStateStore("Data Source=:memory:");
        await Assert.That(async () => { await store.SaveAsync(null!); }).Throws<ArgumentNullException>();
    }
}
