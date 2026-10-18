using System.Net;
using System.Text;
using System.Text.Json;
using McpServer.Trello.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace McpServer.Trello.Tests;

/// <summary>Hosts the real Trello server in-process against a fake Trello API and connects an MCP client to it.</summary>
public abstract class TrelloServerTestBase
{
    protected const string AllowedBoard = "aaaaaaaaaaaaaaaaaaaaaaaa";
    protected const string OtherBoard = "bbbbbbbbbbbbbbbbbbbbbbbb";
    protected const string ListId = "111111111111111111111111";
    protected const string CardId = "222222222222222222222222";

    private WebApplicationFactory<Program>? _factory;
    private McpClient? _client;

    protected FakeTrelloApi Trello { get; } = new();

    /// <summary>Comma-separated allowlist; set before the first call. Empty allows every board.</summary>
    protected string AllowedBoardIds { get; set; } = "";

    protected async Task<McpClient> ClientAsync()
    {
        if (_client is not null)
            return _client;

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Trello:ApiKey", "test-key");
            b.UseSetting("Trello:Token", "test-token");
            b.UseSetting("Trello:AllowedBoardIds", AllowedBoardIds);
            b.UseSetting("Trello:MaxTextChars", "1000");
            b.ConfigureTestServices(services =>
                services.AddHttpClient<TrelloClient>().ConfigurePrimaryHttpMessageHandler(() => Trello));
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

    protected static string Card(string boardId = AllowedBoard, string id = CardId, string name = "Pay invoice") =>
        $$"""
        {"id":"{{id}}","name":"{{name}}","idBoard":"{{boardId}}","idList":"{{ListId}}","due":"2026-10-31T06:00:00.000Z","dueComplete":false,"labels":[{"id":"333333333333333333333333","name":"Urgent","color":"red"}],"shortUrl":"https://trello.com/c/x","dateLastActivity":"2026-10-12T00:00:00.000Z"}
        """;
}

public sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Body, string? Authorization)
{
    public string Path => Uri.AbsolutePath;
    public string Query => Uri.UnescapeDataString(Uri.Query);
    public Dictionary<string, string> Form =>
        (Body ?? "").Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
}

/// <summary>Records every request and answers with <see cref="Respond"/>.</summary>
public sealed class FakeTrelloApi : HttpMessageHandler
{
    public List<RecordedRequest> Requests { get; } = [];

    public Func<RecordedRequest, HttpResponseMessage> Respond { get; set; } = _ => Json("[]");

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new RecordedRequest(request.Method, request.RequestUri!, body,
            request.Headers.TryGetValues("Authorization", out var auth) ? string.Join("", auth) : null);
        Requests.Add(recorded);
        return Respond(recorded);
    }
}
