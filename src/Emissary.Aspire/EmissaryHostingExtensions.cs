using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Emissary.Aspire;

/// <summary>
/// Registers an Emissary agent the way an Aspire client integration does: configuration first,
/// telemetry subscribed, health reported.
/// </summary>
/// <remarks>
/// <para>
/// This package takes no dependency on Aspire itself — it follows the client-integration
/// conventions, so it works in any .NET host and lights the Aspire dashboard up when there is one.
/// Aspire's own integrations are built the same way.
/// </para>
/// <para>
/// The one thing it does that is easy to forget by hand is subscribe to Emissary's
/// <see cref="EmissaryTelemetry.SourceName">ActivitySource and Meter</see>. Emissary emits GenAI
/// spans and metrics whether or not anything listens; without that subscription an agent simply
/// looks untraced, which is indistinguishable from an agent that is not running.
/// </para>
/// </remarks>
public static class EmissaryHostingExtensions
{
    /// <summary>The configuration section read by default: <c>Emissary</c>.</summary>
    public const string DefaultSectionName = "Emissary";

    /// <summary>
    /// Registers <see cref="AgentOptions"/> and a <see cref="ClaudeAgent"/> as singletons, binds the
    /// options from configuration, subscribes Emissary's traces and metrics to OpenTelemetry, and
    /// adds a health check.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <param name="configure">
    /// Applied after configuration, so code wins over settings. This is where tools, rules,
    /// authorizers and handoffs are added — those are objects, not strings, so they cannot come from
    /// a configuration file.
    /// </param>
    /// <param name="sectionName">The configuration section to read; <c>Emissary</c> by default.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A configured value cannot be parsed.</exception>
    public static IHostApplicationBuilder AddEmissaryAgent(
        this IHostApplicationBuilder builder,
        Action<AgentOptions>? configure = null,
        string sectionName = DefaultSectionName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(sectionName);

        var section = builder.Configuration.GetSection(sectionName);

        builder.Services.AddEmissary(options =>
        {
            Bind(section, options);
            configure?.Invoke(options);
        });

        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddSource(EmissaryTelemetry.SourceName))
            .WithMetrics(metrics => metrics.AddMeter(EmissaryTelemetry.MeterName));

        // Whether a key exists is decided once, here, rather than read from the environment inside
        // the check: a health check that answers differently depending on ambient process state is
        // not a health check.
        bool keyConfigured = !string.IsNullOrWhiteSpace(section["ApiKey"])
            || !string.IsNullOrWhiteSpace(builder.Configuration["ANTHROPIC_API_KEY"]);

        builder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
            "emissary",
            provider => new EmissaryHealthCheck(
                provider.GetRequiredService<AgentOptions>(),
                keyConfigured),
            failureStatus: null,
            tags: ["emissary", "ai"]));

        return builder;
    }

    /// <summary>
    /// Copies the scalar settings a configuration file can carry. Read explicitly rather than
    /// reflectively bound: <see cref="AgentOptions"/> holds delegates and tool objects that no
    /// configuration provider can produce, and reflective binding is not AOT-safe.
    /// </summary>
    private static void Bind(IConfigurationSection section, AgentOptions options)
    {
        options.Model = section["Model"] ?? options.Model;
        options.SystemPrompt = section["SystemPrompt"] ?? options.SystemPrompt;
        options.ApiKey = section["ApiKey"] ?? options.ApiKey;
        options.OutputSchemaJson = section["OutputSchemaJson"] ?? options.OutputSchemaJson;
        options.MaxTokens = Integer(section, "MaxTokens") ?? options.MaxTokens;
        options.MaxTurns = Integer(section, "MaxTurns") ?? options.MaxTurns;
        options.MaxHandoffs = Integer(section, "MaxHandoffs") ?? options.MaxHandoffs;
        options.MaxParallelTools = Integer(section, "MaxParallelTools") ?? options.MaxParallelTools;
        options.TokenBudget = Integer64(section, "TokenBudget") ?? options.TokenBudget;
        options.Thinking = Enumeration<ThinkingMode>(section, "Thinking") ?? options.Thinking;
        options.Mode = Enumeration<ExecutionMode>(section, "Mode") ?? options.Mode;
        options.PromptCaching = Enumeration<PromptCacheMode>(section, "PromptCaching") ?? options.PromptCaching;
        options.Effort = Enumeration<EffortLevel>(section, "Effort") ?? options.Effort;
    }

    private static int? Integer(IConfigurationSection section, string key) =>
        section[key] is not { } raw
            ? null
            : int.TryParse(raw, CultureInfo.InvariantCulture, out int value)
                ? value
                : throw Invalid(section, key, raw, "a whole number");

    private static long? Integer64(IConfigurationSection section, string key) =>
        section[key] is not { } raw
            ? null
            : long.TryParse(raw, CultureInfo.InvariantCulture, out long value)
                ? value
                : throw Invalid(section, key, raw, "a whole number");

    private static TEnum? Enumeration<TEnum>(IConfigurationSection section, string key)
        where TEnum : struct, Enum =>
        section[key] is not { } raw
            ? null
            : Enum.TryParse(raw, ignoreCase: true, out TEnum value)
                ? value
                : throw Invalid(section, key, raw, $"one of {string.Join(", ", Enum.GetNames<TEnum>())}");

    /// <summary>
    /// A misspelled setting throws rather than falling back to the default, because a token budget
    /// that silently becomes "unlimited" is discovered on an invoice.
    /// </summary>
    private static InvalidOperationException Invalid(
        IConfigurationSection section,
        string key,
        string raw,
        string expected) =>
        new($"Configuration '{section.Path}:{key}' is '{raw}', which is not {expected}.");
}
