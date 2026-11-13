using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.UserProxy;

namespace RockBot.Agent;

/// <summary>
/// Handles <see cref="WispAnalysisTriggerRequestMessage"/> by asking
/// <see cref="IDreamCycleTrigger"/> to run the wisp-success-analysis and
/// provisional-validation passes now, scoped by the message's fields. Mirrors
/// <see cref="ActiveStatusRequestHandler"/>'s shape.
/// </summary>
internal sealed class WispAnalysisTriggerRequestHandler(
    IDreamCycleTrigger dreamCycleTrigger,
    IMessagePublisher publisher,
    ILogger<WispAnalysisTriggerRequestHandler> logger) : IMessageHandler<WispAnalysisTriggerRequestMessage>
{
    public async Task HandleAsync(WispAnalysisTriggerRequestMessage message, MessageHandlerContext context)
    {
        var replyTo = context.Envelope.ReplyTo;
        var correlationId = context.Envelope.CorrelationId;
        var ct = context.CancellationToken;

        if (string.IsNullOrEmpty(replyTo))
        {
            logger.LogWarning("WispAnalysisTriggerRequestMessage received with no replyTo — ignoring");
            return;
        }

        var request = new Host.WispAnalysisTriggerRequest(
            TargetShapeHashes: message.TargetShapeHashes,
            DescriptionContains: message.DescriptionContains,
            OperatorHint: message.OperatorHint);

        var outcome = await dreamCycleTrigger.TriggerWispAnalysisAsync(request, ct);

        var response = new WispAnalysisTriggerResponseMessage
        {
            Ran = outcome.Ran,
            SkippedReason = outcome.SkippedReason,
            CandidatesAttached = outcome.CandidatesAttached,
            ProvisionalPromoted = outcome.ProvisionalPromoted,
            ProvisionalFlaggedForRepair = outcome.ProvisionalFlaggedForRepair,
            ProvisionalRemoved = outcome.ProvisionalRemoved,
            ProvisionalMarkedStale = outcome.ProvisionalMarkedStale,
            ProvisionalConsidered = outcome.ProvisionalConsidered,
        };
        var envelope = response.ToEnvelope<WispAnalysisTriggerResponseMessage>(
            source: context.Agent.Name, correlationId: correlationId);

        await publisher.PublishAsync(replyTo, envelope, ct);

        logger.LogInformation(
            "WispAnalysisTriggerRequestMessage handled — ran={Ran} attached={Attached} promoted={Promoted}",
            outcome.Ran, outcome.CandidatesAttached, outcome.ProvisionalPromoted);
    }
}
