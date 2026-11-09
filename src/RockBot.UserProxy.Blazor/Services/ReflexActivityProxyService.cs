using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RockBot.Messaging;
using RockBot.UserProxy;

namespace RockBot.UserProxy.Blazor.Services;

/// <summary>
/// Lean, one-way broadcast subscriber feeding <see cref="ReflexActivityStateService"/> —
/// unlike <see cref="UserProxyService"/>, there's no request/response correlation here, just
/// "append whatever arrives". See <c>design/cognitive-tiers.md</c>, "Visibility".
/// </summary>
public sealed class ReflexActivityProxyService(
    IMessageSubscriber subscriber,
    IOptions<UserProxyOptions> options,
    ReflexActivityStateService state,
    ILogger<ReflexActivityProxyService> logger) : IHostedService
{
    private ISubscription? _subscription;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var topic = $"{UserProxyTopics.ReflexFired}.{options.Value.AgentName}";
        _subscription = await subscriber.SubscribeAsync(
            topic, $"reflex-activity.{options.Value.ProxyId}", HandleAsync, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }

    private Task<MessageResult> HandleAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        try
        {
            var evt = envelope.GetPayload<ReflexFiredEvent>();
            if (evt is not null)
                state.Append(evt);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse a reflex-fired event");
        }

        return Task.FromResult(MessageResult.Ack);
    }
}
