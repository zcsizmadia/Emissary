using Emissary;
using Emissary.Aspire;
using Emissary.AspNetCore;
using OpenTelemetry;

var builder = WebApplication.CreateBuilder(args);

// One call does the three things it is easy to get wrong by hand: bind the agent's settings from
// configuration, subscribe Emissary's ActivitySource and Meter to OpenTelemetry, and register a
// health check that reports configuration without calling — or paying — the API.
//
// Tools are objects, so they are added here rather than in appsettings.json.
builder.AddEmissaryAgent(options =>
{
    options.Tools.Add(WeatherTools.GetForecastTool);
    options.SystemPrompt = "You are a concise assistant. Use the tools you are given.";
});

// The app host injects OTEL_EXPORTER_OTLP_ENDPOINT, which this reads. Everything Emissary emits now
// arrives in the Aspire dashboard: run spans, one span per model call, one per tool call, and the
// token, cache and latency metrics.
builder.Services.AddOpenTelemetry().UseOtlpExporter();

builder.Services.AddHealthChecks();

var app = builder.Build();

// POST {"message": "..."} and read the run back as Server-Sent Events.
app.MapEmissaryAgent("/agent");
app.MapHealthChecks("/health");

app.MapGet("/", () => Results.Text(
    """
    Emissary + Aspire.

      POST /agent   {"message":"What is the weather in Oslo?"}   -> Server-Sent Events
      GET  /health                                               -> configuration health

    Then open the Aspire dashboard's Traces and Metrics tabs.
    """));

app.Run();

/// <summary>A tool with an obvious latency, so the tool-duration histogram has something to show.</summary>
internal static partial class WeatherTools
{
    /// <summary>Gets a short forecast for a city.</summary>
    /// <param name="city">The city to forecast, for example Oslo.</param>
    [ClaudeTool]
    public static async Task<string> GetForecast(string city)
    {
        await Task.Delay(Random.Shared.Next(50, 400));
        return $"{city}: 13 °C, light rain, wind 4 m/s.";
    }
}
