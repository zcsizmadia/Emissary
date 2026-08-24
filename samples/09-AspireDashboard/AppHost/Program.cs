// The app host's whole job here is to run the agent service with an API key it does not commit and
// an OTLP endpoint it does not have to configure. Aspire injects the endpoint; the dashboard then
// shows Emissary's own spans and metrics with no exporter code in the service.
var builder = DistributedApplication.CreateBuilder(args);

// A secret parameter, so the key lives in user secrets rather than in this file:
//   dotnet user-secrets --project samples/09-AspireDashboard/AppHost \
//     set Parameters:anthropic-api-key sk-ant-...
var apiKey = builder.AddParameter("anthropic-api-key", secret: true);

builder.AddProject<Projects.AspireDashboardService>("agent")
    .WithEnvironment("ANTHROPIC_API_KEY", apiKey)
    .WithHttpHealthCheck("/health");

builder.Build().Run();
