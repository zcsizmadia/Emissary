namespace Emissary;

/// <summary>
/// Per-million-token prices for a model, in any currency (typically USD). Supply the rates from
/// your Anthropic contract — Emissary ships no hardcoded prices, since they change over time.
/// </summary>
/// <param name="InputPerMillion">Price per 1,000,000 uncached input tokens.</param>
/// <param name="OutputPerMillion">Price per 1,000,000 output tokens.</param>
/// <param name="CacheWritePerMillion">Price per 1,000,000 cache-write (creation) input tokens.</param>
/// <param name="CacheReadPerMillion">Price per 1,000,000 cache-read input tokens.</param>
public sealed record ModelPricing(
    decimal InputPerMillion,
    decimal OutputPerMillion,
    decimal CacheWritePerMillion,
    decimal CacheReadPerMillion);

/// <summary>
/// Computes the monetary cost of agent runs from token usage and a registered price table.
/// Cache reads and writes are billed at their own rates, so a cache-heavy run costs far less
/// than its raw input-token count suggests.
/// </summary>
public sealed class CostEstimator
{
    private readonly Dictionary<string, ModelPricing> _pricing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers (or overrides) the price table for a model.</summary>
    /// <param name="model">The model id, e.g. "claude-opus-5".</param>
    /// <param name="pricing">The per-million-token rates.</param>
    public CostEstimator Register(string model, ModelPricing pricing)
    {
        ArgumentException.ThrowIfNullOrEmpty(model);
        ArgumentNullException.ThrowIfNull(pricing);
        _pricing[model] = pricing;
        return this;
    }

    /// <summary>Estimates the cost of the given usage for a model.</summary>
    /// <param name="model">The model id, which must have been registered.</param>
    /// <param name="usage">The accumulated usage (e.g. <see cref="AgentResult.Usage"/>).</param>
    /// <exception cref="KeyNotFoundException">No pricing is registered for the model.</exception>
    public decimal Estimate(string model, AgentUsage usage)
    {
        ArgumentException.ThrowIfNullOrEmpty(model);
        ArgumentNullException.ThrowIfNull(usage);
        if (!_pricing.TryGetValue(model, out var pricing))
        {
            throw new KeyNotFoundException(
                $"No pricing registered for model '{model}'. Call Register(\"{model}\", ...) first.");
        }

        return Compute(usage, pricing);
    }

    /// <summary>Attempts to estimate the cost; returns <see langword="false"/> if the model is unregistered.</summary>
    /// <param name="model">The model id.</param>
    /// <param name="usage">The accumulated usage.</param>
    /// <param name="cost">The estimated cost when the model is registered.</param>
    public bool TryEstimate(string model, AgentUsage usage, out decimal cost)
    {
        ArgumentException.ThrowIfNullOrEmpty(model);
        ArgumentNullException.ThrowIfNull(usage);
        if (_pricing.TryGetValue(model, out var pricing))
        {
            cost = Compute(usage, pricing);
            return true;
        }

        cost = 0m;
        return false;
    }

    private static decimal Compute(AgentUsage usage, ModelPricing pricing) =>
        (usage.InputTokens * pricing.InputPerMillion
            + usage.OutputTokens * pricing.OutputPerMillion
            + usage.CacheCreationInputTokens * pricing.CacheWritePerMillion
            + usage.CacheReadInputTokens * pricing.CacheReadPerMillion) / 1_000_000m;
}
