using System.Net;
using System.Net.Http;

namespace Emissary.Tests.OpenApi;

/// <summary>
/// A handler that answers every request from a canned response and remembers what it was asked,
/// so a generated tool can be checked against the request it actually put on the wire.
/// </summary>
internal sealed class StubHttp : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    public List<string?> Bodies { get; } = [];

    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    public string Response { get; set; } = """{"ok":true}""";

    public Uri LastUri => Requests[^1].RequestUri!;

    public HttpClient Client(string? baseAddress = null) => new(this)
    {
        BaseAddress = baseAddress is null ? null : new Uri(baseAddress),
    };

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken));

        return new HttpResponseMessage(Status) { Content = new StringContent(Response) };
    }
}
