using McpServer.Trello;
using McpServer.Trello.Services;
using McpServer.Trello.Tools;
using McpServer.TodoApp;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<TrelloOptions>()
    .BindConfiguration(TrelloOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddTransient<TrelloAuthHandler>();
builder.Services.AddHttpClient<TrelloClient>(c =>
    {
        c.BaseAddress = new Uri("https://api.trello.com/1/");
        c.Timeout = TimeSpan.FromSeconds(30);
    })
    .AddHttpMessageHandler<TrelloAuthHandler>();

builder.Services.AddHealthChecks();
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<TrelloTools>()
    .WithStrictArguments();

var app = builder.Build();

app.MapHealthChecks("/health");
app.MapMcp();

await app.RunAsync();

public partial class Program;
