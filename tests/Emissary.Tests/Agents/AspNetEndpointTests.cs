using System.Net;
using System.Text;
using Emissary.AspNetCore;
using Emissary.Tests.Agents;
using Emissary.Tests.Tools;
using Emissary.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Emissary.Tests;

/// <summary>
/// A store whose run is always loadable but never claimable — what a caller sees when a concurrent
/// approval claims the run between this request's load and its delete.
/// </summary>
file sealed class AlreadyClaimedStore : IAgentStateStore
{
    private readonly SuspendedRun _run;

    public AlreadyClaimedStore(SuspendedRun run) => _run = run;

    public Task SaveAsync(SuspendedRun run, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<SuspendedRun?> LoadAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        Task.FromResult<SuspendedRun?>(_run);

    public Task<bool> DeleteAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}

public sealed class AspNetEndpointTests
{
    private static async Task<(IHost Host, HttpClient Client, FakeTransport Transport, InMemoryAgentStateStore Store)> StartAsync(
        Action<AgentOptions>? configure = null,
        bool registerStore = true)
    {
        var options = new AgentOptions();
        options.Tools.Add(SampleTools.EchoTool);
        options.Tools.Add(SampleTools.SendPaymentTool);
        configure?.Invoke(options);
        var transport = new FakeTransport();
        var store = new InMemoryAgentStateStore();

        var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton(new ClaudeAgent(options, transport));
                    if (registerStore)
                    {
                        services.AddSingleton<IAgentStateStore>(store);
                    }
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapEmissaryAgent("/agent");
                        endpoints.MapEmissaryApprovals("/agent/approvals");
                    });
                }))
            .StartAsync();

        return (host, host.GetTestServer().CreateClient(), transport, store);
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    [Test]
    public async Task Agent_endpoint_streams_sse_events()
    {
        var (host, client, transport, _) = await StartAsync();
        using var _host = host;
        transport.EnqueueTurn(
            new StreamThinkingDelta("pondering"),
            new StreamTextDelta("Hello"),
            FakeTransport.TextTurn("Hello"));

        var response = await client.PostAsync("/agent", Json("""{"message":"hi"}"""));
        string body = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("text/event-stream");
        await Assert.That(body).Contains("event: thinking");
        await Assert.That(body).Contains("event: text");
        await Assert.That(body).Contains("""{"delta":"Hello"}""");
        await Assert.That(body).Contains("event: completed");
        await Assert.That(body).Contains("\"stopReason\":\"Completed\"");
    }

    [Test]
    public async Task Agent_endpoint_streams_tool_events()
    {
        var (host, client, transport, _) = await StartAsync();
        using var _host = host;
        transport.EnqueueTurn(
            new StreamToolUseStart("t1", "echo"),
            FakeTransport.ToolTurn(FakeTransport.Use("t1", "echo", """{"text":"ping"}""")));
        transport.EnqueueTurn(FakeTransport.TextTurn("done"));

        string body = await client.PostAsync("/agent", Json("""{"message":"go"}"""))
            .Result.Content.ReadAsStringAsync();

        await Assert.That(body).Contains("event: tool_call");
        await Assert.That(body).Contains("event: tool_result");
        await Assert.That(body).Contains("\"result\":\"ping\"");
    }

    [Test]
    public async Task Missing_message_is_a_bad_request()
    {
        var (host, client, _, _) = await StartAsync();
        using var _host = host;

        await Assert.That((await client.PostAsync("/agent", Json("{}"))).StatusCode)
            .IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await client.PostAsync("/agent", Json("null"))).StatusCode)
            .IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Suspension_persists_and_approval_webhook_resumes()
    {
        var (host, client, transport, store) = await StartAsync(options =>
            options.ApprovalRequired = tool => tool.Privileged);
        using var _host = host;
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "send_payment", """{"amount":75}""")));
        transport.EnqueueTurn(FakeTransport.TextTurn("payment complete"));

        string first = await (await client.PostAsync("/agent", Json("""{"message":"pay"}""")))
            .Content.ReadAsStringAsync();
        await Assert.That(first).Contains("event: suspended");
        await Assert.That(first).Contains("\"stopReason\":\"AwaitingApproval\"");

        // The suspension landed in the store; approve it via the webhook.
        string conversationId = first.Split("\"conversationId\":\"")[1].Split('"')[0];
        await Assert.That((await store.LoadAsync(Guid.Parse(conversationId)))).IsNotNull();

        var approval = await client.PostAsync("/agent/approvals",
            Json($$"""{"conversationId":"{{conversationId}}","approve":true}"""));
        string resumed = await approval.Content.ReadAsStringAsync();

        await Assert.That(approval.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(resumed).Contains("\"result\":\"sent 75\"");
        await Assert.That(resumed).Contains("\"finalText\":\"payment complete\"");
        await Assert.That(await store.LoadAsync(Guid.Parse(conversationId))).IsNull();
    }

    [Test]
    public async Task Two_approvals_of_the_same_run_do_not_both_execute_it()
    {
        var (host, client, transport, store) = await StartAsync(options =>
            options.ApprovalRequired = tool => tool.Privileged);
        using var _host = host;
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "send_payment", """{"amount":75}""")));
        transport.EnqueueTurn(FakeTransport.TextTurn("payment complete"));

        string first = await (await client.PostAsync("/agent", Json("""{"message":"pay"}""")))
            .Content.ReadAsStringAsync();
        string conversationId = first.Split("\"conversationId\":\"")[1].Split('"')[0];
        var body = Json($$"""{"conversationId":"{{conversationId}}","approve":true}""");

        var winner = await client.PostAsync("/agent/approvals", body);
        var loser = await client.PostAsync("/agent/approvals",
            Json($$"""{"conversationId":"{{conversationId}}","approve":true}"""));

        // The payment must not be sent twice because an approval webhook was retried or
        // double-clicked. The second request finds nothing left to resume.
        await Assert.That(winner.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(loser.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await store.LoadAsync(Guid.Parse(conversationId))).IsNull();
    }

    [Test]
    public async Task Losing_the_claim_race_is_a_conflict_not_a_second_execution()
    {
        // Both requests loaded the run before either deleted it — the interleaving the sequential
        // test above cannot produce. The loser must not resume.
        var (host, client, transport, store) = await StartAsync(options =>
            options.ApprovalRequired = tool => tool.Privileged);
        using var _host = host;
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "send_payment", """{"amount":75}""")));

        string first = await (await client.PostAsync("/agent", Json("""{"message":"pay"}""")))
            .Content.ReadAsStringAsync();
        string conversationId = first.Split("\"conversationId\":\"")[1].Split('"')[0];
        var run = await store.LoadAsync(Guid.Parse(conversationId));

        var (raceHost, raceClient) = await StartWithStoreAsync(new AlreadyClaimedStore(run!));
        using var _raceHost = raceHost;

        var response = await raceClient.PostAsync("/agent/approvals",
            Json($$"""{"conversationId":"{{conversationId}}","approve":true}"""));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
    }

    private static async Task<(IHost Host, HttpClient Client)> StartWithStoreAsync(IAgentStateStore store)
    {
        var options = new AgentOptions();
        options.Tools.Add(SampleTools.SendPaymentTool);
        var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton(new ClaudeAgent(options, new FakeTransport()));
                    services.AddSingleton(store);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapEmissaryApprovals("/agent/approvals"));
                }))
            .StartAsync();

        return (host, host.GetTestServer().CreateClient());
    }

    [Test]
    public async Task The_sse_response_asks_proxies_not_to_buffer_it()
    {
        var (host, client, transport, _) = await StartAsync();
        using var _host = host;
        transport.EnqueueTurn(FakeTransport.TextTurn("hi"));

        var response = await client.PostAsync("/agent", Json("""{"message":"hi"}"""));

        // Without this a buffering proxy delivers the whole run at once, and streaming is a lie.
        await Assert.That(response.Headers.GetValues("X-Accel-Buffering").Single()).IsEqualTo("no");
        await Assert.That(response.Headers.CacheControl!.NoCache).IsTrue();
    }

    [Test]
    public async Task A_failure_after_the_headers_are_sent_becomes_an_error_event()
    {
        var (host, client, transport, _) = await StartAsync();
        using var _host = host;

        // Streams one event, then faults — so the response has already been committed.
        transport.EnqueueTurn(new StreamTextDelta("partial"));

        string body = await (await client.PostAsync("/agent", Json("""{"message":"go"}""")))
            .Content.ReadAsStringAsync();

        await Assert.That(body).Contains("event: text");
        await Assert.That(body).Contains("event: error");
        await Assert.That(body).Contains("InvalidOperationException");
    }

    [Test]
    public async Task A_client_that_hangs_up_mid_stream_is_not_an_error()
    {
        var blocked = new TaskCompletionSource();
        var (host, client, transport, _) = await StartAsync(options =>
            options.Tools.Add(new ToolDefinition(
                "block", "Blocks.", """{"type":"object","properties":{}}""",
                async (_, token) =>
                {
                    blocked.TrySetResult();
                    await Task.Delay(Timeout.Infinite, token);
                    return "unreachable";
                })));
        using var _host = host;
        transport.EnqueueTurn(
            new StreamToolUseStart("t1", "block"),
            FakeTransport.ToolTurn(FakeTransport.Use("t1", "block", "{}")));

        using var cancellation = new CancellationTokenSource();
        var post = client.PostAsync("/agent", Json("""{"message":"go"}"""), cancellation.Token);

        await blocked.Task;
        await cancellation.CancelAsync();

        // Disconnecting is ordinary: it must not surface as a server fault.
        await Assert.ThrowsAsync<TaskCanceledException>(async () => await post);
    }

    [Test]
    public async Task Tool_failures_and_handoffs_reach_the_client()
    {
        var specialistTransport = new FakeTransport();
        specialistTransport.EnqueueTurn(FakeTransport.TextTurn("billing handled it"));
        var specialist = new ClaudeAgent(new AgentOptions(), specialistTransport);

        var (host, client, transport, _) = await StartAsync(options =>
        {
            options.Tools.Add(new ToolDefinition(
                "break", "Always fails.", """{"type":"object","properties":{}}""",
                (_, _) => throw new TimeoutException("gateway down")));
            options.Handoffs.Add(new HandoffTarget("billing", specialist, "Billing."));
        });
        using var _host = host;
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "break", "{}")));
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t2", "handoff_to_billing", "{}")));

        string body = await (await client.PostAsync("/agent", Json("""{"message":"go"}""")))
            .Content.ReadAsStringAsync();

        // Both were silently dropped by the endpoint before.
        await Assert.That(body).Contains("event: tool_failed");
        await Assert.That(body).Contains("\"exceptionType\":\"TimeoutException\"");
        await Assert.That(body).DoesNotContain("gateway down");
        await Assert.That(body).Contains("event: handoff");
        await Assert.That(body).Contains("\"targetName\":\"billing\"");
    }

    [Test]
    public async Task Suspension_streams_even_without_a_store()
    {
        var (host, client, transport, _) = await StartAsync(
            options => options.ApprovalRequired = tool => tool.Privileged,
            registerStore: false);
        using var _host = host;
        transport.EnqueueTurn(FakeTransport.ToolTurn(FakeTransport.Use("t1", "send_payment", """{"amount":1}""")));

        string body = await (await client.PostAsync("/agent", Json("""{"message":"pay"}""")))
            .Content.ReadAsStringAsync();

        await Assert.That(body).Contains("event: suspended");
        await Assert.That(body).Contains("\"pendingTools\":[\"send_payment\"]");
    }

    [Test]
    public async Task Approval_webhook_validates_and_404s()
    {
        var (host, client, _, _) = await StartAsync();
        using var _host = host;

        await Assert.That((await client.PostAsync("/agent/approvals", Json("null"))).StatusCode)
            .IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await client.PostAsync("/agent/approvals",
                Json("""{"conversationId":"00000000-0000-0000-0000-000000000000","approve":true}"""))).StatusCode)
            .IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await client.PostAsync("/agent/approvals",
                Json($$"""{"conversationId":"{{Guid.CreateVersion7()}}","approve":true}"""))).StatusCode)
            .IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Map_methods_validate_arguments()
    {
        await Assert.That(() => EmissaryEndpoints.MapEmissaryAgent(null!, "/x")).Throws<ArgumentNullException>();
        await Assert.That(() => EmissaryEndpoints.MapEmissaryApprovals(null!, "/x")).Throws<ArgumentNullException>();
    }
}
