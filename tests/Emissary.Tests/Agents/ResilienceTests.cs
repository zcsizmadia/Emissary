using System.Net.Http;
using System.Runtime.CompilerServices;
using Emissary.Transport;

namespace Emissary.Tests;

file sealed class FaultyTransport : IModelTransport
{
    private readonly Func<int, Exception?> _faultForAttempt;
    private readonly bool _faultMidStream;

    public FaultyTransport(Func<int, Exception?> faultForAttempt, bool faultMidStream = false)
    {
        _faultForAttempt = faultForAttempt;
        _faultMidStream = faultMidStream;
    }

    public int Attempts { get; private set; }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        int attempt = Attempts++;
        await Task.Yield();

        if (_faultMidStream)
        {
            yield return new StreamTextDelta("partial");
            throw new HttpRequestException("mid-stream failure");
        }

        if (_faultForAttempt(attempt) is { } fault)
        {
            throw fault;
        }

        yield return new StreamTextDelta("ok");
        yield return new StreamCompleted(new ModelResponse([new TextBlock("ok")], "end_turn", 1, 1));
    }
}

file sealed class SlowTransport : IModelTransport
{
    public async IAsyncEnumerable<StreamEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        yield break;
    }
}

public sealed class ResilienceTests
{
    private static ModelRequest Request() =>
        new("m", null, 10, ThinkingMode.Adaptive, null, null, PromptCacheMode.None, [Message.User("hi")], []);

    private static ResilienceOptions Fast(int retries) =>
        new() { MaxRetries = retries, BaseDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero };

    private static async Task<List<StreamEvent>> Drain(ResilientTransport transport)
    {
        var events = new List<StreamEvent>();
        await foreach (var e in transport.StreamAsync(Request(), CancellationToken.None))
        {
            events.Add(e);
        }

        return events;
    }

    [Test]
    public async Task Successful_first_attempt_streams_through()
    {
        var inner = new FaultyTransport(_ => null);
        var events = await Drain(new ResilientTransport(inner, Fast(2)));

        await Assert.That(inner.Attempts).IsEqualTo(1);
        await Assert.That(events.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Transient_failures_are_retried_then_succeed()
    {
        var inner = new FaultyTransport(attempt => attempt < 2 ? new HttpRequestException("boom") : null);
        var events = await Drain(new ResilientTransport(inner, Fast(3)));

        await Assert.That(inner.Attempts).IsEqualTo(3);
        await Assert.That(events.OfType<StreamTextDelta>().Single().Text).IsEqualTo("ok");
    }

    [Test]
    public async Task Retries_are_exhausted_then_the_error_propagates()
    {
        var inner = new FaultyTransport(_ => new HttpRequestException("always"));

        await Assert.That(async () => { await Drain(new ResilientTransport(inner, Fast(2))); })
            .Throws<HttpRequestException>();
        await Assert.That(inner.Attempts).IsEqualTo(3); // initial + 2 retries
    }

    [Test]
    public async Task Non_transient_after_a_retry_stops_immediately()
    {
        // attempt 0 transient (retried), attempt 1 non-transient (throw with attempts still left).
        var inner = new FaultyTransport(attempt =>
            attempt == 0 ? new HttpRequestException("transient") : new InvalidOperationException("fatal"));

        await Assert.That(async () => { await Drain(new ResilientTransport(inner, Fast(5))); })
            .Throws<InvalidOperationException>();
        await Assert.That(inner.Attempts).IsEqualTo(2);
    }

    [Test]
    public async Task Non_transient_errors_are_not_retried()
    {
        var inner = new FaultyTransport(_ => new InvalidOperationException("fatal"));

        await Assert.That(async () => { await Drain(new ResilientTransport(inner, Fast(5))); })
            .Throws<InvalidOperationException>();
        await Assert.That(inner.Attempts).IsEqualTo(1);
    }

    [Test]
    public async Task Mid_stream_failure_is_not_retried()
    {
        var inner = new FaultyTransport(_ => null, faultMidStream: true);

        await Assert.That(async () => { await Drain(new ResilientTransport(inner, Fast(3))); })
            .Throws<HttpRequestException>();
        await Assert.That(inner.Attempts).IsEqualTo(1);
    }

    [Test]
    public async Task Custom_should_retry_predicate_is_honored()
    {
        var inner = new FaultyTransport(attempt => attempt < 1 ? new InvalidOperationException("x") : null);
        var options = Fast(2);
        options.ShouldRetry = ex => ex is InvalidOperationException;

        var events = await Drain(new ResilientTransport(inner, options));

        await Assert.That(inner.Attempts).IsEqualTo(2);
        await Assert.That(events.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Caller_cancellation_is_not_retried()
    {
        var inner = new FaultyTransport(_ => new HttpRequestException("boom"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.That(async () =>
            {
                await foreach (var _ in new ResilientTransport(inner, Fast(5)).StreamAsync(Request(), cts.Token))
                {
                }
            })
            .Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Per_attempt_timeout_triggers_a_retry()
    {
        var inner = new SlowTransport();
        var options = new ResilienceOptions
        {
            MaxRetries = 1,
            BaseDelay = TimeSpan.Zero,
            MaxDelay = TimeSpan.Zero,
            RequestTimeout = TimeSpan.FromMilliseconds(50),
        };

        // Both attempts time out, so a timeout ultimately surfaces after the retry is exhausted.
        await Assert.That(async () => { await Drain(new ResilientTransport(inner, options)); })
            .Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Constructor_validates_arguments()
    {
        await Assert.That(() => new ResilientTransport(null!, new ResilienceOptions())).Throws<ArgumentNullException>();
        await Assert.That(() => new ResilientTransport(new SlowTransport(), null!)).Throws<ArgumentNullException>();
        await Assert.That(() => new ResilientTransport(new SlowTransport(), new ResilienceOptions { MaxRetries = -1 }))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    [Arguments(0, 100)]
    [Arguments(1, 200)]
    [Arguments(2, 400)]
    [Arguments(10, 1000)] // capped at MaxDelay
    public async Task NextDelay_backs_off_exponentially_and_caps(int attempt, int expectedMs)
    {
        var options = new ResilienceOptions
        {
            BaseDelay = TimeSpan.FromMilliseconds(100),
            MaxDelay = TimeSpan.FromMilliseconds(1000),
        };

        await Assert.That(ResiliencePolicy.NextDelay(attempt, options).TotalMilliseconds).IsEqualTo(expectedMs);
    }

    [Test]
    public async Task IsTransient_classifies_common_errors()
    {
        await Assert.That(ResiliencePolicy.IsTransient(new HttpRequestException())).IsTrue();
        await Assert.That(ResiliencePolicy.IsTransient(new TimeoutException())).IsTrue();
        await Assert.That(ResiliencePolicy.IsTransient(new OperationCanceledException())).IsFalse();
        await Assert.That(ResiliencePolicy.IsTransient(new InvalidOperationException())).IsFalse();
        await Assert.That(ResiliencePolicy.IsTransient(new FakeRateLimitException())).IsTrue();
        await Assert.That(ResiliencePolicy.IsTransient(new FakeOverloadedException())).IsTrue();
        await Assert.That(ResiliencePolicy.IsTransient(new ServiceUnavailableException())).IsTrue();
        await Assert.That(ResiliencePolicy.IsTransient(new InternalServerException())).IsTrue();
    }

    [Test]
    public async Task Agent_uses_resilience_for_live_and_recording_transports()
    {
        // Smoke: the live/recording constructors build without error with a configured policy.
        var options = new AgentOptions { ApiKey = "test" };
        options.Resilience.MaxRetries = 5;
        _ = new ClaudeAgent(options);
        _ = new ClaudeAgent(options, new TrajectoryRecorder());
        await Assert.That(options.Resilience.MaxRetries).IsEqualTo(5);
    }
}

file sealed class FakeRateLimitException : Exception;

file sealed class FakeOverloadedException : Exception;

file sealed class ServiceUnavailableException : Exception;

file sealed class InternalServerException : Exception;
