namespace Emissary.Tests;

public sealed class CostEstimatorTests
{
    private static CostEstimator Estimator() => new CostEstimator()
        .Register("claude-opus-5", new ModelPricing(
            InputPerMillion: 15m, OutputPerMillion: 75m, CacheWritePerMillion: 18.75m, CacheReadPerMillion: 1.5m));

    [Test]
    public async Task Estimates_cost_across_all_token_tiers()
    {
        var usage = new AgentUsage(
            InputTokens: 1_000_000, OutputTokens: 1_000_000,
            CacheCreationInputTokens: 1_000_000, CacheReadInputTokens: 1_000_000);

        decimal cost = Estimator().Estimate("claude-opus-5", usage);

        // 15 + 75 + 18.75 + 1.5
        await Assert.That(cost).IsEqualTo(110.25m);
    }

    [Test]
    public async Task Cache_reads_are_far_cheaper_than_raw_input()
    {
        var estimator = Estimator();
        var cached = new AgentUsage(0, 0, 0, 1_000_000);
        var uncached = new AgentUsage(1_000_000, 0, 0, 0);

        await Assert.That(estimator.Estimate("claude-opus-5", cached)).IsEqualTo(1.5m);
        await Assert.That(estimator.Estimate("claude-opus-5", uncached)).IsEqualTo(15m);
    }

    [Test]
    public async Task Zero_usage_costs_nothing()
    {
        await Assert.That(Estimator().Estimate("claude-opus-5", AgentUsage.Zero)).IsEqualTo(0m);
    }

    [Test]
    public async Task Model_ids_are_case_insensitive()
    {
        await Assert.That(Estimator().Estimate("Claude-Opus-5", new AgentUsage(1_000_000, 0))).IsEqualTo(15m);
    }

    [Test]
    public async Task Register_overrides_existing_pricing()
    {
        var estimator = Estimator().Register("claude-opus-5", new ModelPricing(1m, 1m, 1m, 1m));

        await Assert.That(estimator.Estimate("claude-opus-5", new AgentUsage(1_000_000, 0))).IsEqualTo(1m);
    }

    [Test]
    public async Task Unregistered_model_throws_from_Estimate()
    {
        await Assert.That(() => Estimator().Estimate("unknown-model", AgentUsage.Zero))
            .Throws<KeyNotFoundException>()
            .WithMessageContaining("unknown-model");
    }

    [Test]
    public async Task TryEstimate_reports_registration_status()
    {
        var estimator = Estimator();

        await Assert.That(estimator.TryEstimate("claude-opus-5", new AgentUsage(1_000_000, 0), out var known)).IsTrue();
        await Assert.That(known).IsEqualTo(15m);
        await Assert.That(estimator.TryEstimate("nope", AgentUsage.Zero, out var missing)).IsFalse();
        await Assert.That(missing).IsEqualTo(0m);
    }

    [Test]
    public async Task Arguments_are_validated()
    {
        var estimator = Estimator();
        await Assert.That(() => estimator.Register("", new ModelPricing(1, 1, 1, 1))).Throws<ArgumentException>();
        await Assert.That(() => estimator.Register("m", null!)).Throws<ArgumentNullException>();
        await Assert.That(() => estimator.Estimate("", AgentUsage.Zero)).Throws<ArgumentException>();
        await Assert.That(() => estimator.Estimate("m", null!)).Throws<ArgumentNullException>();
        await Assert.That(() => estimator.TryEstimate("", AgentUsage.Zero, out _)).Throws<ArgumentException>();
    }
}
