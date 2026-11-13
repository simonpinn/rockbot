using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.UserProxy;

namespace RockBot.Agent;

/// <summary>
/// Handles <see cref="DreamCycleTriggerRequest"/> by asking <see cref="IDreamCycleTrigger"/>
/// to start a full dream cycle now. Mirrors <see cref="ActiveStatusRequestHandler"/>'s shape.
/// </summary>
internal sealed class DreamCycleTriggerRequestHandler(
    IDreamCycleTrigger dreamCycleTrigger,
    IMessagePublisher publisher,
    ILogger<DreamCycleTriggerRequestHandler> logger) : IMessageHandler<DreamCycleTriggerRequest>
{
    public async Task HandleAsync(DreamCycleTriggerRequest message, MessageHandlerContext context)
    {
        var replyTo = context.Envelope.ReplyTo;
        var correlationId = context.Envelope.CorrelationId;
        var ct = context.CancellationToken;

        if (string.IsNullOrEmpty(replyTo))
        {
            logger.LogWarning("DreamCycleTriggerRequest received with no replyTo — ignoring");
            return;
        }

        var outcome = await dreamCycleTrigger.TriggerFullCycleAsync(ct);

        var response = new DreamCycleTriggerResponse { Outcome = outcome.ToString() };
        var envelope = response.ToEnvelope<DreamCycleTriggerResponse>(
            source: context.Agent.Name, correlationId: correlationId);

        await publisher.PublishAsync(replyTo, envelope, ct);

        logger.LogInformation("DreamCycleTriggerRequest handled — outcome={Outcome}", outcome);
    }
}
