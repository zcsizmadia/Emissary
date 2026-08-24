using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Emissary;

/// <summary>
/// The "Emissary" ActivitySource and Meter, following the OpenTelemetry GenAI semantic
/// conventions. Subscribe with <see cref="EmissaryTelemetry"/>'s names.
/// </summary>
internal static class EmissaryDiagnostics
{
    // The version stamped by the build (MinVer) — telemetry always reports the real version.
    private static readonly string Version =
        typeof(EmissaryDiagnostics).Assembly.GetName().Version!.ToString(3);

    public static readonly ActivitySource Source = new(EmissaryTelemetry.SourceName, Version);

    public static readonly Meter Meter = new(EmissaryTelemetry.MeterName, Version);

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

    // Tool latency is where an agent's wall-clock time actually goes, and a tool that has become
    // slow is invisible in run duration alone — the model narrates around it and the answer still
    // arrives. Tagged with the outcome so a p99 is not dominated by calls that timed out.
    public static readonly Histogram<double> ToolDuration = Meter.CreateHistogram<double>(
        "emissary.tool.duration", "s", "Wall-clock duration of tool executions.");

    // The two null-conditional lines below are coverage-baselined: the null path (no listener,
    // the common production case) is unreachable under the test host, which installs an ambient
    // global ActivityListener. Verified with a probe test.
    public static void Tag(Activity? activity, string key, object? value) =>
        activity?.SetTag(key, value);

    public static void Fail(Activity? activity, string message) =>
        activity?.SetStatus(ActivityStatusCode.Error, message);
}
