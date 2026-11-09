using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.Tools;

namespace RockBot.Stimulus;

/// <summary>
/// Watches the bus for messages matching a registered <see cref="StimulusPattern"/> and fires
/// the matched reflex tool directly through <see cref="IToolRegistry"/> — no
/// <c>AgentLoopRunner</c> turn, no LLM call, for that invocation. Modeled on the standalone
/// <c>IHostedService</c>-owns-its-own-subscription shape (<c>McpBridgeService</c>,
/// <c>TokenCacheStore</c>) rather than <c>AgentHostBuilder.SubscribeTo</c>/<c>IMessageHandler&lt;T&gt;</c>,
/// since that pipeline assumes one topic maps to one known CLR type — this dispatcher matches
/// raw envelopes against a dynamic, reloadable pattern set instead. The session-less
/// <see cref="ToolInvokeRequest"/> it builds mirrors <c>CapabilityClaimVerifier</c>'s own
/// production precedent for calling a tool executor with no chat session in scope.
/// See <c>design/cognitive-tiers.md</c>.
/// </summary>
internal sealed class StimulusDispatcher(
    StimulusPatternHolder patternHolder,
    IMessageSubscriber subscriber,
    IToolRegistry toolRegistry,
    AgentIdentity agentIdentity,
    ILogger<StimulusDispatcher> logger,
    IXFactorMemoryStore? xFactorStore = null,
    XFactorMemoryOptions? xFactorOptions = null) : IHostedService
{
    private readonly Dictionary<string, ISubscription> _subscriptionsByTopic = new();
    private readonly SemaphoreSlim _reconcileLock = new(1, 1);
    private readonly XFactorMemoryOptions _xFactorOptions = xFactorOptions ?? new XFactorMemoryOptions();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        patternHolder.OnUpdated += OnPatternsUpdated;
        await ReconcileAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        patternHolder.OnUpdated -= OnPatternsUpdated;

        await _reconcileLock.WaitAsync(cancellationToken);
        try
        {
            foreach (var sub in _subscriptionsByTopic.Values)
                await sub.DisposeAsync();
            _subscriptionsByTopic.Clear();
        }
        finally
        {
            _reconcileLock.Release();
        }
    }

    private void OnPatternsUpdated() => _ = ReconcileSafelyAsync();

    private async Task ReconcileSafelyAsync()
    {
        try
        {
            await ReconcileAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Stimulus subscription reconciliation failed after a pattern reload");
        }
    }

    /// <summary>
    /// Subscribes to every distinct topic named by an enabled pattern that isn't already
    /// subscribed, and unsubscribes any topic no longer referenced by any enabled pattern.
    /// </summary>
    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await _reconcileLock.WaitAsync(cancellationToken);
        try
        {
            var neededTopics = patternHolder.Patterns
                .Where(p => p.Enabled)
                .Select(p => p.Topic)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var topic in neededTopics.Except(_subscriptionsByTopic.Keys).ToList())
            {
                var subscriptionName = $"stimulus-dispatcher.{agentIdentity.Name}.{topic}";
                var currentTopic = topic; // captured per-subscription for the handler closure
                var subscription = await subscriber.SubscribeAsync(
                    topic, subscriptionName, (envelope, ct) => HandleMessageAsync(currentTopic, envelope, ct),
                    cancellationToken);
                _subscriptionsByTopic[topic] = subscription;
            }

            foreach (var topic in _subscriptionsByTopic.Keys.Except(neededTopics).ToList())
            {
                await _subscriptionsByTopic[topic].DisposeAsync();
                _subscriptionsByTopic.Remove(topic);
            }
        }
        finally
        {
            _reconcileLock.Release();
        }
    }

    private async Task<MessageResult> HandleMessageAsync(string topic, MessageEnvelope envelope, CancellationToken ct)
    {
        foreach (var pattern in patternHolder.Patterns.Where(p => p.Enabled && p.Topic == topic))
        {
            var match = await StimulusMatcher.TryMatchAsync(
                pattern, topic, envelope, xFactorStore, _xFactorOptions, DateTimeOffset.UtcNow, ct);
            if (!match.Matched)
                continue;

            var executor = toolRegistry.GetExecutor(pattern.ReflexTool);
            if (executor is null)
            {
                logger.LogWarning(
                    "Stimulus '{PatternId}' matched but its reflex tool '{ReflexTool}' is not registered",
                    pattern.Id, pattern.ReflexTool);
                continue;
            }

            var request = new ToolInvokeRequest
            {
                ToolCallId = $"stimulus-{Guid.NewGuid():N}"[..20],
                ToolName = pattern.ReflexTool,
                Arguments = null,
                SessionId = null,
            };

            try
            {
                var invocation = new StimulusInvocation(pattern.Id, topic, match.MatchedChannels, match.XFactor);
                using (StimulusInvocationContext.Set(invocation))
                    await executor.ExecuteAsync(request, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The triggering bus message is legitimate regardless of whether the reflex
                // call itself succeeded - that failure is ReflexExecutor's own concern to log
                // (via IWispExecutionLog), not grounds for a requeue/DLQ storm on this topic.
                logger.LogWarning(ex, "Stimulus '{PatternId}' fire of '{ReflexTool}' failed", pattern.Id, pattern.ReflexTool);
            }
        }

        return MessageResult.Ack;
    }
}
