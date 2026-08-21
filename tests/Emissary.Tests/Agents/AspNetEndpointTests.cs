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
