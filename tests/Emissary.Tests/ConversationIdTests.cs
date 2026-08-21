namespace Emissary.Tests;

public sealed class ConversationIdTests
{
    [Test]
    public async Task New_generates_unique_ids()
    {
        ConversationId first = ConversationId.New();
        ConversationId second = ConversationId.New();

        await Assert.That(first).IsNotEqualTo(second);
    }

    [Test]
    public async Task New_ids_are_time_ordered()
    {
        ConversationId first = ConversationId.New();
        ConversationId second = ConversationId.New();

        await Assert.That(first.Value.Version).IsEqualTo(7);

        // UUIDv7 orders by a millisecond timestamp in the first 48 bits (12 hex chars);
        // within the same millisecond the remaining bits are random, so only the
        // timestamp prefix is guaranteed non-decreasing.
        string firstTimestamp = first.ToString()[..12];
        string secondTimestamp = second.ToString()[..12];
        await Assert.That(string.CompareOrdinal(firstTimestamp, secondTimestamp)).IsLessThanOrEqualTo(0);
    }

    [Test]
    public async Task ToString_is_compact_hex()
    {
        var id = new ConversationId(Guid.Parse("0198c5f2-1111-7222-8333-444455556666"));

        await Assert.That(id.ToString()).IsEqualTo("0198c5f2111172228333444455556666");
    }

    [Test]
    public async Task Parse_round_trips_compact_form()
    {
        ConversationId original = ConversationId.New();

        ConversationId parsed = ConversationId.Parse(original.ToString());

        await Assert.That(parsed).IsEqualTo(original);
    }

    [Test]
    public async Task Parse_rejects_invalid_text()
    {
        await Assert.That(() => ConversationId.Parse("not-a-guid")).Throws<FormatException>();
    }

    [Test]
    public async Task TryParse_accepts_valid_text()
    {
        ConversationId original = ConversationId.New();

        bool ok = ConversationId.TryParse(original.ToString(), out ConversationId parsed);

        await Assert.That(ok).IsTrue();
        await Assert.That(parsed).IsEqualTo(original);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("not-a-guid")]
    public async Task TryParse_rejects_invalid_text(string? text)
    {
        bool ok = ConversationId.TryParse(text, out ConversationId parsed);

        await Assert.That(ok).IsFalse();
        await Assert.That(parsed).IsEqualTo(default(ConversationId));
    }

    [Test]
    public async Task Equality_follows_underlying_value()
    {
        var value = Guid.CreateVersion7();

        await Assert.That(new ConversationId(value)).IsEqualTo(new ConversationId(value));
        await Assert.That(new ConversationId(value) == new ConversationId(value)).IsTrue();
        await Assert.That(new ConversationId(value) != ConversationId.New()).IsTrue();
    }
}
