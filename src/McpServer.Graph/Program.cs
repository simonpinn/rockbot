using Azure.Core;
using Azure.Identity;
using McpServer.Graph;
using McpServer.Graph.Services;
using McpServer.Graph.Tools;
using McpServer.TodoApp;
using Microsoft.Extensions.Options;

// Attachments may declare legacy charsets such as windows-1252.
System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<GraphMailOptions>()
    .BindConfiguration(GraphMailOptions.SectionName)
    .ValidateDataAnnotations()
    .Validate(o => GraphTimeZone.Resolve(o.TimeZone) is not null, "Graph:TimeZone must be an IANA or Windows time zone, e.g. Australia/Sydney")
    .Validate(o => GraphDriveOptionsValidation.Validate(o.Drives) is null,
        "Graph:Drives is misconfigured; see the Drive alias validation rules in GraphDriveOptions.cs")
    .ValidateOnStart();

// Compose sets Graph__Drives__N__Alias for every slot it declares, so an unused slot still binds
// as an entry with an empty Alias rather than being absent. Drop those before validation runs, so
// a deployment with no drives configured doesn't fail startup over empty placeholder slots.
builder.Services.PostConfigure<GraphMailOptions>(o => o.Drives = o.Drives.Where(d => !string.IsNullOrWhiteSpace(d.Alias)).ToList());

builder.Services.AddSingleton<TokenCredential>(sp =>
{
    var o = sp.GetRequiredService<IOptions<GraphMailOptions>>().Value;
    return new ClientSecretCredential(o.TenantId, o.ClientId, o.ClientSecret);
});
builder.Services.AddTransient<GraphAuthHandler>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<GraphTimeZone>();
builder.Services.AddTransient<GraphMailClient>();
builder.Services.AddTransient<GraphCalendarClient>();
builder.Services.AddTransient<GraphContactsClient>();
builder.Services.AddSingleton<GraphDriveClient>();
builder.Services.AddHttpClient<GraphApi>(c =>
    {
        c.BaseAddress = new Uri("https://graph.microsoft.com/v1.0/");
        c.Timeout = TimeSpan.FromSeconds(30);
    })
    .AddHttpMessageHandler<GraphAuthHandler>();

builder.Services.AddHealthChecks();
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<MailTools>()
    .WithTools<CalendarTools>()
    .WithTools<ContactsTools>()
    .WithTools<DriveTools>()
    .WithStrictArguments();

var app = builder.Build();

app.MapHealthChecks("/health");
app.MapMcp();

await app.RunAsync();

public partial class Program;
