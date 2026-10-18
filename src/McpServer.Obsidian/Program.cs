using McpServer.Obsidian;
using McpServer.Obsidian.Services;
using McpServer.Obsidian.Tools;
using McpServer.TodoApp;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<ObsidianOptions>()
    .BindConfiguration(ObsidianOptions.SectionName)
    .ValidateDataAnnotations()
    .Validate(o => Directory.Exists(o.VaultPath), "Obsidian:VaultPath must be an existing directory")
    .Validate(o => Vault.IsSafeRelativePath(o.WriteFolder), "Obsidian:WriteFolder must be a relative folder with no '..' or hidden segments")
    .Validate(o => Vault.TryFindZone(o.TimeZone) is not null, "Obsidian:TimeZone is not a known time zone")
    .ValidateOnStart();

builder.Services.AddSingleton<Vault>();

builder.Services.AddHealthChecks();
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<VaultTools>()
    .WithStrictArguments();

var app = builder.Build();

app.MapHealthChecks("/health");
app.MapMcp();

await app.RunAsync();

public partial class Program;
