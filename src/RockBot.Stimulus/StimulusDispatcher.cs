using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    IOptions<XFactorMemoryOptions>? xFactorOptions = null) : IHostedService
{
    private readonly Dictionary<string, ISubscription> _subscriptionsByTopic = new();
    private readonly SemaphoreSlim _reconcileLock = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastFiredAtByPatternId = new();
    private bool _stopped;
    // Only IOptions<XFactorMemoryOptions> is ever registered (by WithXFactorMemory) - a bare
    // XFactorMemoryOptions ctor param would silently resolve to null via DI and this would
    // fall back to built-in defaults regardless of what was configured.
    private readonly XFactorMemoryOptions _xFactorOptions = xFactorOptions?.Value ?? new XFactorMemoryOptions();

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
            // Set while still holding the lock: a reconcile that was already queued behind
            // this wait (a reload fired just before shutdown) acquires the lock next, sees
            // this flag, and bails out instead of resubscribing to an emptied map with
            // nothing left to ever tear the new subscriptions back down.
            _stopped = true;
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
    /// Internal (not private) so a test can call it directly to simulate a reconcile that
    /// arrives after <see cref="StopAsync"/> has already completed.
    /// </summary>
    internal async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await _reconcileLock.WaitAsync(cancellationToken);
        try
        {
            if (_stopped)
                return;

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
            try
            {
                // Matching (which may do real store I/O for an x-factor-slotted pattern) and
                // execution are both inside this try: a transient store failure is exactly as
                // "not the triggering message's fault" as a failed reflex call below, and must
                // not dead-letter an otherwise-legitimate bus message either.
                var match = await StimulusMatcher.TryMatchAsync(
                    pattern, topic, envelope, xFactorStore, _xFactorOptions, DateTimeOffset.UtcNow, ct);
                if (!match.Matched)
                    continue;

                if (IsThrottled(pattern))
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

                var invocation = new StimulusInvocation(pattern.Id, topic, match.MatchedChannels, match.XFactor);
                using (StimulusInvocationContext.Set(invocation))
                    await executor.ExecuteAsync(request, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The triggering bus message is legitimate regardless of whether matching or
                // the reflex call itself succeeded - that failure is this pattern's own concern
                // to log, not grounds for a requeue/DLQ storm on this topic.
                logger.LogWarning(ex, "Stimulus '{PatternId}' evaluation against '{ReflexTool}' failed", pattern.Id, pattern.ReflexTool);
            }
        }

        return MessageResult.Ack;
    }

    /// <summary>
    /// True when <paramref name="pattern"/> fired within its own <see cref="StimulusPattern.MinIntervalSeconds"/>
    /// window and must be skipped this time. Atomic per pattern id: a throttled attempt never
    /// moves the window forward, so a burst of messages arriving faster than the interval can't
    /// starve the pattern of ever firing again by continually pushing the window out.
    /// </summary>
    private bool IsThrottled(StimulusPattern pattern)
    {
        if (pattern.MinIntervalSeconds is not { } minIntervalSeconds)
            return false;

        var now = DateTimeOffset.UtcNow;
        var minInterval = TimeSpan.FromSeconds(minIntervalSeconds);
        var throttled = false;

        _lastFiredAtByPatternId.AddOrUpdate(
            pattern.Id,
            _ => now,
            (_, lastFiredAt) =>
            {
                if (now - lastFiredAt < minInterval)
                {
                    throttled = true;
                    return lastFiredAt;
                }
                return now;
            });

        return throttled;
    }
}
