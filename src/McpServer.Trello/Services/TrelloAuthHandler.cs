using Microsoft.Extensions.Options;

namespace McpServer.Trello.Services;

/// <summary>Sends the key and token in Trello's Authorization header so they never appear in URLs.</summary>
public sealed class TrelloAuthHandler(IOptions<TrelloOptions> options) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var o = options.Value;
        request.Headers.TryAddWithoutValidation("Authorization", $"OAuth oauth_consumer_key=\"{o.ApiKey}\", oauth_token=\"{o.Token}\"");
        return base.SendAsync(request, cancellationToken);
    }
}
