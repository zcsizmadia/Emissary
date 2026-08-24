using System.Diagnostics;
using System.Diagnostics.Metrics;
using Emissary.Aspire;
using Emissary.Tests.Agents;
using Emissary.Tests.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Emissary.Tests.Aspire;

/// <summary>
/// The client integration's job is to make the boring half correct: settings read from
/// configuration, telemetry actually subscribed, and health reported without spending money.
/// </summary>
public sealed class EmissaryHostingTests
{
    private static HostApplicationBuilder Builder(params (string Key, string Value)[] settings)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(
            settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)));
        return builder;
    }

    private static AgentOptions Resolve(HostApplicationBuilder builder) =>
        builder.Build().Services.GetRequiredService<AgentOptions>();

    [Test]
    public async Task Every_scalar_setting_can_come_from_configuration()
    {
        var builder = Builder(
            ("Emissary:Model", "claude-configured-1"),
            ("Emissary:SystemPrompt", "Be brief."),
            ("Emissary:ApiKey", "sk-from-config"),
            ("Emissary:OutputSchemaJson", """{"type":"object"}"""),
            ("Emissary:MaxTokens", "2048"),
            ("Emissary:MaxTurns", "4"),
            ("Emissary:MaxHandoffs", "1"),
            ("Emissary:MaxParallelTools", "2"),
            ("Emissary:TokenBudget", "50000"),
            ("Emissary:Thinking", "disabled"),      // case-insensitive on purpose
            ("Emissary:Mode", "Shadow"),
            ("Emissary:PromptCaching", "None"),
            ("Emissary:Effort", "Low"));

        builder.AddEmissaryAgent();
        var options = Resolve(builder);

        await Assert.That(options.Model).IsEqualTo("claude-configured-1");
        await Assert.That(options.SystemPrompt).IsEqualTo("Be brief.");
        await Assert.That(options.ApiKey).IsEqualTo("sk-from-config");
        await Assert.That(options.OutputSchemaJson).IsEqualTo("""{"type":"object"}""");
        await Assert.That(options.MaxTokens).IsEqualTo(2048);
        await Assert.That(options.MaxTurns).IsEqualTo(4);
        await Assert.That(options.MaxHandoffs).IsEqualTo(1);
        await Assert.That(options.MaxParallelTools).IsEqualTo(2);
        await Assert.That(options.TokenBudget).IsEqualTo(50_000L);
        await Assert.That(options.Thinking).IsEqualTo(ThinkingMode.Disabled);
        await Assert.That(options.Mode).IsEqualTo(ExecutionMode.Shadow);
        await Assert.That(options.PromptCaching).IsEqualTo(PromptCacheMode.None);
        await Assert.That(options.Effort).IsEqualTo(EffortLevel.Low);
    }

    [Test]
    public async Task An_empty_configuration_leaves_every_default_alone()
    {
        var builder = Builder();
        builder.AddEmissaryAgent();

        var options = Resolve(builder);
        var untouched = new AgentOptions();

        await Assert.That(options.Model).IsEqualTo(untouched.Model);
        await Assert.That(options.SystemPrompt).IsNull();
        await Assert.That(options.ApiKey).IsNull();
        await Assert.That(options.OutputSchemaJson).IsNull();
        await Assert.That(options.MaxTokens).IsEqualTo(untouched.MaxTokens);
        await Assert.That(options.MaxTurns).IsEqualTo(untouched.MaxTurns);
        await Assert.That(options.MaxHandoffs).IsEqualTo(untouched.MaxHandoffs);
        await Assert.That(options.MaxParallelTools).IsNull();
        await Assert.That(options.TokenBudget).IsNull();
        await Assert.That(options.Thinking).IsEqualTo(untouched.Thinking);
        await Assert.That(options.Mode).IsEqualTo(untouched.Mode);
        await Assert.That(options.PromptCaching).IsEqualTo(untouched.PromptCaching);
        await Assert.That(options.Effort).IsNull();
    }

    [Test]
    public async Task Code_wins_over_configuration_and_is_where_tools_are_added()
    {
        // Tools, rules and authorizers are objects; no configuration provider can produce one.
        var builder = Builder(("Agent:Model", "from-config"), ("Agent:MaxTurns", "9"));

        builder.AddEmissaryAgent(
            options =>
            {
                options.Model = "from-code";
                options.Tools.Add(SampleTools.EchoTool);
            },
            sectionName: "Agent");

        var options = Resolve(builder);
        await Assert.That(options.Model).IsEqualTo("from-code");
        await Assert.That(options.MaxTurns).IsEqualTo(9);          // still read from the section
        await Assert.That(options.Tools.Single().Name).IsEqualTo("echo");
    }

    [Test]
    [Arguments("Emissary:MaxTurns", "fifty", "not a whole number")]
    [Arguments("Emissary:TokenBudget", "lots", "not a whole number")]
    [Arguments("Emissary:Thinking", "loud", "not one of ")]
    public async Task A_misspelled_setting_throws_and_names_it(string key, string value, string expected)
    {
        // Silently falling back to the default is how a token budget becomes "unlimited" and is
        // discovered on an invoice. It throws during registration rather than on first resolve, so
        // the process fails to start instead of failing on someone's first request.
        var builder = Builder((key, value));

        var thrown = Assert.Throws<InvalidOperationException>(() => builder.AddEmissaryAgent());

        await Assert.That(thrown!.Message).Contains($"'{key}' is '{value}'");
        await Assert.That(thrown.Message).Contains(expected);
    }

    [Test]
    public async Task Registration_validates_its_arguments()
    {
        await Assert.That(() => ((IHostApplicationBuilder)null!).AddEmissaryAgent())
            .Throws<ArgumentNullException>();
        await Assert.That(() => Builder().AddEmissaryAgent(sectionName: null!))
            .Throws<ArgumentNullException>();
        await Assert.That(EmissaryHostingExtensions.DefaultSectionName).IsEqualTo("Emissary");
    }

    [Test]
    [Arguments("claude-x", "sk-key", null, HealthStatus.Healthy, "no API call was made")]
    [Arguments("claude-x", null, null, HealthStatus.Unhealthy, "No API key is configured")]
    [Arguments("claude-x", null, "sk-env", HealthStatus.Healthy, "no API call was made")]
    [Arguments("", "sk-key", null, HealthStatus.Unhealthy, "No model is configured")]
    public async Task The_health_check_reports_configuration_without_calling_the_api(
        string model,
        string? configuredKey,
        string? environmentKey,
        HealthStatus expected,
        string because)
    {
        var settings = new List<(string, string)> { ("Emissary:Model", model) };
        if (configuredKey is not null)
        {
            settings.Add(("Emissary:ApiKey", configuredKey));
        }

        if (environmentKey is not null)
        {
            settings.Add(("ANTHROPIC_API_KEY", environmentKey));
        }

        var builder = Builder([.. settings]);
        builder.AddEmissaryAgent();
        using var host = builder.Build();

        var report = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        var entry = report.Entries["emissary"];
        await Assert.That(entry.Status).IsEqualTo(expected);
        await Assert.That(entry.Description).Contains(because);
        await Assert.That(entry.Tags).Contains("ai");
    }

    [Test]
    public async Task Traces_and_metrics_reach_the_exporter()
    {
        // The assertion that matters: without AddSource/AddMeter an agent looks untraced, which is
        // indistinguishable from an agent that is not running. So export for real and look.
        var spans = new List<Activity>();
        var exported = new List<Metric>();

        var builder = Builder(("Emissary:Model", "aspire-test-model"), ("Emissary:ApiKey", "sk-x"));
        builder.AddEmissaryAgent(options => options.Tools.Add(SampleTools.EchoTool));
        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddInMemoryExporter(spans))
            .WithMetrics(metrics => metrics.AddInMemoryExporter(exported));

        using var host = builder.Build();
        var tracerProvider = host.Services.GetRequiredService<TracerProvider>();
        var meterProvider = host.Services.GetRequiredService<MeterProvider>();

        // The registered agent would need a real API key, so this run uses the same options with a
        // fake transport — the telemetry path is identical.
        var options = host.Services.GetRequiredService<AgentOptions>();
        var transport = new FakeTransport();
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "echo", """{"text":"x"}""")));
        transport.EnqueueTurn(FakeTransport.TextTurn("done"));
        await new ClaudeAgent(options, transport).RunAsync("go");

        tracerProvider.ForceFlush();
        meterProvider.ForceFlush();

        await Assert.That(spans.Select(s => s.OperationName))
            .Contains(name => name.StartsWith("invoke_agent", StringComparison.Ordinal));
        await Assert.That(spans.Select(s => s.Source.Name)).Contains(EmissaryTelemetry.SourceName);

        // Tool latency is new in this change, and it is the metric an agent's wall-clock time
        // actually hides in.
        await Assert.That(exported.Select(m => m.Name)).Contains("emissary.tool.duration");
        await Assert.That(exported.Select(m => m.MeterName).Distinct())
            .Contains(EmissaryTelemetry.MeterName);
    }
}
