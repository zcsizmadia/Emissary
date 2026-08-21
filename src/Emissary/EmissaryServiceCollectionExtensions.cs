using Microsoft.Extensions.DependencyInjection;

namespace Emissary;

/// <summary>Dependency-injection registration for Emissary.</summary>
public static class EmissaryServiceCollectionExtensions
{
    /// <summary>Registers a configured <see cref="ClaudeAgent"/> (and its <see cref="AgentOptions"/>) as singletons.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the agent options.</param>
    public static IServiceCollection AddEmissary(this IServiceCollection services, Action<AgentOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new AgentOptions();
        configure(options);
        services.AddSingleton(options);
        services.AddSingleton(static provider => new ClaudeAgent(provider.GetRequiredService<AgentOptions>()));
        return services;
    }
}
