namespace Emissary;

/// <summary>
/// The names to subscribe to for Emissary's traces and metrics.
/// </summary>
/// <remarks>
/// <para>
/// Emissary follows the OpenTelemetry GenAI semantic conventions, but none of that reaches a
/// collector until something subscribes:
/// </para>
/// <code>
/// builder.Services.AddOpenTelemetry()
///     .WithTracing(t => t.AddSource(EmissaryTelemetry.SourceName))
///     .WithMetrics(m => m.AddMeter(EmissaryTelemetry.MeterName));
/// </code>
/// <para>
/// Forgetting that step is the single most common reason an agent looks untraced, so the names are
/// part of the public surface rather than a string in a documentation page. They are constants
/// because a rename would invalidate every dashboard and alert built on them; they will not change.
/// </para>
/// </remarks>
public static class EmissaryTelemetry
{
    /// <summary>The <see cref="System.Diagnostics.ActivitySource"/> name: <c>Emissary</c>.</summary>
    public const string SourceName = "Emissary";

    /// <summary>The <see cref="System.Diagnostics.Metrics.Meter"/> name: <c>Emissary</c>.</summary>
    public const string MeterName = "Emissary";
}
