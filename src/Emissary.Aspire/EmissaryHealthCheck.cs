using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Emissary.Aspire;

/// <summary>
/// Reports whether this process is configured to run an agent.
/// </summary>
/// <remarks>
/// It deliberately makes no API call. A health check runs on a schedule, and a check that talked to
/// the model would bill you for being alive — worse, it would report the provider's health rather
/// than this application's. What it can tell you is the thing that actually breaks a deployment: a
/// missing key or an empty model, which otherwise surfaces as a 401 on the first real request.
/// </remarks>
internal sealed class EmissaryHealthCheck : IHealthCheck
{
    private readonly AgentOptions _options;
    private readonly bool _keyConfigured;

    public EmissaryHealthCheck(AgentOptions options, bool keyConfigured)
    {
        _options = options;
        _keyConfigured = keyConfigured;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.Model))
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "No model is configured. Set Emissary:Model."));
        }

        if (!_keyConfigured)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "No API key is configured. Set Emissary:ApiKey or the ANTHROPIC_API_KEY "
                + "environment variable."));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            $"Configured for '{_options.Model}' with {_options.Tools.Count} tool(s); "
            + "no API call was made."));
    }
}
