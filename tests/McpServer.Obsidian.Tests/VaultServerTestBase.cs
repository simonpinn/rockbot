using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace McpServer.Obsidian.Tests;

/// <summary>Hosts the real Obsidian server in-process over a temporary vault and connects an MCP client to it.</summary>
public abstract class VaultServerTestBase
{
    protected const string WriteFolder = "RockBot";
    protected const string VaultName = "Main Vault";

    private WebApplicationFactory<Program>? _factory;
    private McpClient? _client;

    protected string VaultDir { get; } = Directory.CreateTempSubdirectory("obsidian-vault-").FullName;

    /// <summary>Extra settings applied when the server starts, e.g. a smaller read limit.</summary>
    protected Dictionary<string, string> Settings { get; } = [];

    protected string Write(string rel, string text)
    {
        var full = Path.Combine(VaultDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    protected string ReadFile(string rel) => File.ReadAllText(Path.Combine(VaultDir, rel));

    protected bool Exists(string rel) => File.Exists(Path.Combine(VaultDir, rel));

    protected async Task<McpClient> ClientAsync()
    {
        if (_client is not null)
            return _client;

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Obsidian:VaultPath", VaultDir);
            b.UseSetting("Obsidian:WriteFolder", WriteFolder);
            b.UseSetting("Obsidian:VaultName", VaultName);
            b.UseSetting("Obsidian:TimeZone", "Australia/Sydney");
            foreach (var (key, value) in Settings)
                b.UseSetting(key, value);
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
        Directory.Delete(VaultDir, recursive: true);
    }

    protected async Task<CallToolResult> CallAsync(string tool, Dictionary<string, object?>? arguments = null) =>
        await (await ClientAsync()).CallToolAsync(tool, arguments);

    protected async Task<JsonElement> CallOkAsync(string tool, Dictionary<string, object?>? arguments = null)
    {
        var result = await CallAsync(tool, arguments);
        Assert.IsFalse(result.IsError ?? false, $"{tool} failed: {TextOf(result)}");
        return JsonDocument.Parse(TextOf(result)).RootElement.Clone();
    }

    protected async Task<string> CallErrorAsync(string tool, Dictionary<string, object?> arguments)
    {
        var result = await CallAsync(tool, arguments);
        Assert.IsTrue(result.IsError ?? false, $"{tool} should have failed but returned: {TextOf(result)}");
        return TextOf(result);
    }

    protected static string TextOf(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
}
