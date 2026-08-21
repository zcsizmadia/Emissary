using Anthropic.Models.Messages;
using Emissary.Tests.Tools;
using Emissary.Transport;

namespace Emissary.Tests;

public sealed class PromptCachingTests
{
    private static ModelRequest Request(PromptCacheMode caching, IReadOnlyList<Emissary.Message> messages) =>
        new("claude-opus-5", "Be terse.", 1024, ThinkingMode.Adaptive, null, null, caching,
            messages, [SampleTools.EchoTool, SampleTools.AddTool]);

    private static Tool ToolAt(MessageCreateParams parameters, int index) =>
        (Tool)parameters.Tools![index].Value!;

    private static T Block<T>(MessageCreateParams parameters, int message) =>
        (T)((IReadOnlyList<ContentBlockParam>)parameters.Messages[message].Content.Value!)[0].Value!;

    [Test]
    public async Task Automatic_caching_places_all_three_breakpoints()
    {
        var parameters = AnthropicMapper.ToCreateParams(Request(PromptCacheMode.Automatic,
        [
            Emissary.Message.User("first"),
            new Emissary.Message(MessageRole.Assistant, [new Emissary.TextBlock("reply")]),
            Emissary.Message.User("second"),
        ]));

        await Assert.That(ToolAt(parameters, 0).CacheControl).IsNull();
        await Assert.That(ToolAt(parameters, 1).CacheControl).IsNotNull();
        await Assert.That(parameters.System).IsNotNull();

        var lastBlock = Block<TextBlockParam>(parameters, 2);
        await Assert.That(lastBlock.CacheControl).IsNotNull();
        var earlierBlock = Block<TextBlockParam>(parameters, 0);
        await Assert.That(earlierBlock.CacheControl).IsNull();
    }

    [Test]
    public async Task Caching_off_places_no_breakpoints()
    {
        var parameters = AnthropicMapper.ToCreateParams(Request(PromptCacheMode.None,
            [Emissary.Message.User("only")]));

        await Assert.That(ToolAt(parameters, 0).CacheControl).IsNull();
        await Assert.That(ToolAt(parameters, 1).CacheControl).IsNull();
        var block = Block<TextBlockParam>(parameters, 0);
        await Assert.That(block.CacheControl).IsNull();
    }

    [Test]
    public async Task Cached_breakpoint_lands_on_tool_results_too()
    {
        var parameters = AnthropicMapper.ToCreateParams(Request(PromptCacheMode.Automatic,
        [
            Emissary.Message.User("go"),
            new Emissary.Message(MessageRole.User, [new Emissary.ToolResultBlock("t1", "ok", false)]),
        ]));

        var lastBlock = Block<ToolResultBlockParam>(parameters, 1);
        await Assert.That(lastBlock.CacheControl).IsNotNull();
    }

    [Test]
    public async Task Uncacheable_final_block_is_skipped_gracefully()
    {
        var parameters = AnthropicMapper.ToCreateParams(Request(PromptCacheMode.Automatic,
        [
            new Emissary.Message(MessageRole.Assistant, [new Emissary.ThinkingBlock("hmm", "sig")]),
        ]));

        // ThinkingBlockParam has no CacheControl at all — the mapper must not try to attach one.
        var lastBlock = Block<ThinkingBlockParam>(parameters, 0);
        await Assert.That(lastBlock.Thinking).IsEqualTo("hmm");
    }

    [Test]
    public async Task Cacheless_system_prompt_maps_to_plain_string()
    {
        await Assert.That(AnthropicMapper.ToSystem(null, cache: true)).IsNull();
        await Assert.That(AnthropicMapper.ToSystem("s", cache: false)).IsNotNull();
        await Assert.That(AnthropicMapper.ToSystem("s", cache: true)).IsNotNull();
    }
}
