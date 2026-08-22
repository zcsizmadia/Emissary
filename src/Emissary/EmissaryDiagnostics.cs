using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Emissary;

/// <summary>
/// The "Emissary" ActivitySource and Meter, following the OpenTelemetry GenAI semantic
/// conventions. Subscribe with <c>AddSource("Emissary")</c> / <c>AddMeter("Emissary")</c>.
/// </summary>
internal static class EmissaryDiagnostics
{
    private const string Name = "Emissary";

    // The version stamped by the build (MinVer) — telemetry always reports the real version.
    private static readonly string Version =
        typeof(EmissaryDiagnostics).Assembly.GetName().Version!.ToString(3);

    public static readonly ActivitySource Source = new(Name, Version);

    public static readonly Meter Meter = new(Name, Version);

    public static readonly Counter<long> InputTokens = Meter.CreateCounter<long>(
        "emissary.usage.input_tokens", "{token}", "Input tokens consumed by model calls.");

    public static readonly Counter<long> OutputTokens = Meter.CreateCounter<long>(
        "emissary.usage.output_tokens", "{token}", "Output tokens produced by model calls.");

    public static readonly Counter<long> CacheCreationTokens = Meter.CreateCounter<long>(
        "emissary.usage.cache_creation_input_tokens", "{token}", "Input tokens written to the prompt cache.");

    public static readonly Counter<long> CacheReadTokens = Meter.CreateCounter<long>(
        "emissary.usage.cache_read_input_tokens", "{token}", "Input tokens served from the prompt cache.");

    public static readonly Counter<long> ToolCalls = Meter.CreateCounter<long>(
        "emissary.tool.calls", "{call}", "Tool invocations requested by the model.");

    public static readonly Histogram<double> RunDuration = Meter.CreateHistogram<double>(
        "emissary.run.duration", "s", "Wall-clock duration of complete agent runs.");

    // The two null-conditional lines below are coverage-baselined: the null path (no listener,
    // the common production case) is unreachable under the test host, which installs an ambient
    // global ActivityListener. Verified with a probe test.
    public static void Tag(Activity? activity, string key, object? value) =>
        activity?.SetTag(key, value);

    public static void Fail(Activity? activity, string message) =>
        activity?.SetStatus(ActivityStatusCode.Error, message);
}
