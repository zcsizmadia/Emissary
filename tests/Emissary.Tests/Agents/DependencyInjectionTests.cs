using Microsoft.Extensions.DependencyInjection;

namespace Emissary.Tests;

public sealed class DependencyInjectionTests
{
    [Test]
    public async Task AddEmissary_registers_options_and_agent()
    {
        var services = new ServiceCollection();

        services.AddEmissary(options =>
        {
            options.Model = "claude-opus-5";
            options.SystemPrompt = "Be helpful.";
        });
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<AgentOptions>();
        await Assert.That(options.SystemPrompt).IsEqualTo("Be helpful.");
        await Assert.That(provider.GetRequiredService<ClaudeAgent>()).IsNotNull();
    }

    [Test]
    public async Task AddEmissary_validates_arguments()
    {
        await Assert.That(() => EmissaryServiceCollectionExtensions.AddEmissary(null!, _ => { }))
            .Throws<ArgumentNullException>();
        await Assert.That(() => new ServiceCollection().AddEmissary(null!))
            .Throws<ArgumentNullException>();
    }
}
