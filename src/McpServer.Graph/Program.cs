using Azure.Core;
using Azure.Identity;
using McpServer.Graph;
using McpServer.Graph.Services;
using McpServer.Graph.Tools;
using McpServer.TodoApp;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<GraphMailOptions>()
    .BindConfiguration(GraphMailOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<TokenCredential>(sp =>
{
    var o = sp.GetRequiredService<IOptions<GraphMailOptions>>().Value;
    return new ClientSecretCredential(o.TenantId, o.ClientId, o.ClientSecret);
});
builder.Services.AddTransient<GraphAuthHandler>();
builder.Services.AddHttpClient<GraphMailClient>(c =>
    {
        c.BaseAddress = new Uri("https://graph.microsoft.com/v1.0/");
        c.Timeout = TimeSpan.FromSeconds(30);
    })
    .AddHttpMessageHandler<GraphAuthHandler>();

builder.Services.AddHealthChecks();
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<MailTools>()
    .WithStrictArguments();

var app = builder.Build();

app.MapHealthChecks("/health");
app.MapMcp();

await app.RunAsync();

public partial class Program;
