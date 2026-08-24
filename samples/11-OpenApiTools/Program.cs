using System.Text.Json;
using Emissary;
using Emissary.OpenApi;

// An agent that drives a real public API with no hand-written tool code. Everything the model can
// call below comes out of the specification: names, schemas, descriptions, and the safety posture.
//
// Open-Meteo needs no API key, so the tool half of this sample runs with no credentials at all.
const string ForecastSpec = """
{
  "openapi": "3.0.3",
  "info": { "title": "Open-Meteo", "version": "1.0" },
  "servers": [{ "url": "https://api.open-meteo.com/v1" }],
  "paths": {
    "/forecast": {
      "get": {
        "operationId": "forecast",
        "summary": "Current conditions and a daily forecast for one coordinate",
        "parameters": [
          { "name": "latitude", "in": "query", "required": true,
            "description": "Degrees north, e.g. 59.91 for Oslo.", "schema": { "type": "number" } },
          { "name": "longitude", "in": "query", "required": true,
            "description": "Degrees east, e.g. 10.75 for Oslo.", "schema": { "type": "number" } },
          { "name": "current", "in": "query",
            "description": "Comma-separated current fields, e.g. temperature_2m,wind_speed_10m.",
            "schema": { "type": "string" } },
          { "name": "daily", "in": "query",
            "description": "Comma-separated daily fields, e.g. temperature_2m_max.",
            "schema": { "type": "string" } },
          { "name": "forecast_days", "in": "query",
            "description": "How many days to return, 1 to 16.", "schema": { "type": "integer" } },
          { "name": "timezone", "in": "query",
            "description": "IANA zone, or 'auto' to use the coordinate's own zone.",
            "schema": { "type": "string" } }
        ]
      }
    }
  }
}
""";

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

var set = OpenApiTools.FromSpec(
    ForecastSpec,
    http,
    new OpenApiToolOptions { Prefix = "weather_", MaxResultLength = 4_000 });

Console.WriteLine(set.ToText());

// The posture is read out of the document, not configured here: a GET returns content someone else
// wrote, so it taints the run, and any mutating verb in the same specification would be privileged
// and therefore unreachable after this tool has been used.
foreach (var generated in set.Tools)
{
    Console.WriteLine($"{generated.Name}  untrusted={generated.Untrusted}  privileged={generated.Privileged}");
    Console.WriteLine($"  {generated.Description}");
}

var tool = set.Tools[0];
Console.WriteLine();
Console.WriteLine("Generated input schema:");
Console.WriteLine(tool.InputSchemaJson);
Console.WriteLine();

// A generated tool is an ordinary object, so it can be called without a model — which is also how
// you unit-test one.
using var probe = JsonDocument.Parse(
    """{"latitude":59.91,"longitude":10.75,"current":"temperature_2m","timezone":"auto"}""");
Console.WriteLine("[direct] " + await tool.InvokeAsync(probe.RootElement));
Console.WriteLine();

if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
{
    Console.WriteLine("[skipped] Set ANTHROPIC_API_KEY to let the agent choose the arguments itself.");
    return 0;
}

const string Question = "How warm is it in Oslo right now, and is tomorrow warmer?";
Console.WriteLine($"> {Question}");

var options = new AgentOptions
{
    // Capped so a demo run costs a fraction of a cent, and a stuck run stops (SampleBudget).
    Model = SampleBudget.Model,
    // Adaptive thinking is rejected by the small model, so ask for none.
    Thinking = ThinkingMode.Disabled,
    MaxTurns = SampleBudget.MaxTurns,
    TokenBudget = SampleBudget.TokenBudget,
    SystemPrompt =
        "You answer weather questions using the tools you are given. Look the coordinates up from "
        + "your own knowledge, call the tool, and answer in one or two sentences with units.",
};

foreach (var generated in set.Tools)
{
    options.Tools.Add(generated);
}

var result = await new ClaudeAgent(options).RunAsync(Question);

Console.WriteLine(result.FinalText);
Console.WriteLine(
    $"[{result.StopReason}; {result.Usage.InputTokens} in / {result.Usage.OutputTokens} out; "
    + $"tainted by an untrusted read: {result.Tainted}]");

return 0;
