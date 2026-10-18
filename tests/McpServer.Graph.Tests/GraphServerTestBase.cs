using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using McpServer.Graph.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace McpServer.Graph.Tests;

/// <summary>
/// Hosts the real Graph server in-process with a stub credential and a fake Graph endpoint,
/// and connects an MCP client to it.
/// </summary>
public abstract class GraphServerTestBase
{
    protected const string Mailbox = "me@contoso.com";
    protected const string TimeZone = "Australia/Sydney";

    private WebApplicationFactory<Program>? _factory;
    private McpClient? _client;

    protected FakeGraphHandler Graph { get; } = new();

    protected async Task<McpClient> ClientAsync()
    {
        if (_client is not null)
            return _client;

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Graph:TenantId", "tenant");
            b.UseSetting("Graph:ClientId", "client");
            b.UseSetting("Graph:ClientSecret", "secret");
            b.UseSetting("Graph:Mailbox", Mailbox);
            b.UseSetting("Graph:MaxBodyChars", "1000");
            b.UseSetting("Graph:TimeZone", TimeZone);
            b.ConfigureTestServices(services =>
            {
                services.AddSingleton<TokenCredential>(new StubCredential());
                services.AddHttpClient<GraphApi>().ConfigurePrimaryHttpMessageHandler(() => Graph);
            });
        });

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(_factory.Server.BaseAddress, "/") },
            _factory.CreateClient(),
            ownsHttpClient: true);
        _client = await McpClient.CreateAsync(transport);
        return _client;
    }

    [TestCleanup]
    public async Task CleanupServerAsync()
    {
        if (_client is not null)
            await _client.DisposeAsync();
        if (_factory is not null)
            await _factory.DisposeAsync();
    }

    protected async Task<CallToolResult> CallAsync(string tool, Dictionary<string, object?>? arguments = null) =>
        await (await ClientAsync()).CallToolAsync(tool, arguments);

    protected async Task<JsonElement> CallOkAsync(string tool, Dictionary<string, object?>? arguments = null)
    {
        var result = await CallAsync(tool, arguments);
        Assert.IsFalse(result.IsError ?? false, $"{tool} failed: {TextOf(result)}");
        return JsonDocument.Parse(TextOf(result)).RootElement.Clone();
    }

    protected static string TextOf(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    private sealed class StubCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("test-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(GetToken(requestContext, cancellationToken));
    }
}

/// <summary>Records every request and answers with <see cref="Respond"/>.</summary>
public sealed class FakeGraphHandler : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Each request's body, captured before the client disposes the request.</summary>
    public List<string?> Bodies { get; } = [];

    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => Json("""{"value":[]}""");

    public HttpRequestMessage Single => Requests.Single();

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public JsonElement BodyOf(int index) => JsonDocument.Parse(Bodies[index]!).RootElement.Clone();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
        return Respond(request);
    }
}
