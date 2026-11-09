using System.Text.Json;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.Tools;
using RockBot.UserProxy;
using RockBot.Wisp;

namespace RockBot.Tools.Reflex;

/// <summary>
/// The single shared <see cref="IToolExecutor"/> for every registered reflex tool. Looks the
/// call up in <see cref="IReflexRegistrationLookup"/>, fetches the resource's current body fresh
/// from <see cref="ISkillStore"/> on every call (so a corrected <c>attach</c> or a restamped
/// <c>demote-provisional</c> from Phase 1's repair path is picked up automatically, with no lease
/// or cache to invalidate), and replays it through the same <see cref="WispReplayPipeline"/>
/// <c>spawn_wisps</c> uses. See <c>design/reflex-tools.md</c> Phase 2.
/// </summary>
internal sealed class ReflexExecutor(
    IReflexRegistrationLookup lookup,
    ISkillStore skillStore,
    WispExecutor wispExecutor,
    IWispExecutionLog? executionLog,
    WispDispatchCircuitBreaker? circuitBreaker,
    IMcpToolDirectory? mcpToolDirectory,
    ILogger<ReflexExecutor> logger,
    IMessagePublisher? publisher = null,
    AgentIdentity? agentIdentity = null) : IToolExecutor
{
    public async Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        // Captured before any work runs, matching SpawnWispsExecutor.ExecuteAsync - the #686
        // ledger/completion-evaluator contract a reflex call must honor exactly like spawn_wisps.
        var outcome = ToolCallOutcomeContext.Value;

        if (!lookup.TryGet(request.ToolName, out var skillName, out var filename))
            return Error(request, $"Reflex tool '{request.ToolName}' is no longer registered.");

        var body = await skillStore.GetResourceAsync(skillName, filename);
        if (body is null)
            return Error(request, $"Resource '{filename}' on skill '{skillName}' no longer exists.");

        WispDefinition definition;
        try
        {
            definition = JsonSerializer.Deserialize<WispDefinition>(body)
                ?? throw new JsonException("deserialized to null");
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex,
                "Reflex '{ToolName}': stored wisp body for '{Skill}/{File}' is not valid",
                request.ToolName, skillName, filename);
            return Error(request, $"Stored wisp body for '{skillName}/{filename}' is invalid: {ex.Message}");
        }

        if (definition.Steps is null or { Count: 0 })
            return Error(request, $"Stored wisp body for '{skillName}/{filename}' has no steps.");

        var wispId = $"reflex-{Guid.NewGuid():N}"[..16];
        var prepared = WispReplayPipeline.Prepare(definition, mcpToolDirectory);
        var (result, tripped) = await WispReplayPipeline.RunAsync(
            wispExecutor, prepared, wispId, request.SessionId, circuitBreaker, logger, ct);

        if (!tripped)
        {
            if (executionLog is not null)
            {
                try
                {
                    var failedStep = result.FailedStep;
                    await executionLog.AppendAsync(new WispExecutionRecord
                    {
                        WispId = result.WispId,
                        Description = result.Definition.Description,
                        DefinitionHash = prepared.DefinitionHash,
                        ShapeHash = prepared.ShapeHash,
                        Succeeded = result.IsSuccess,
                        StepCount = result.Definition.Steps.Count,
                        StepsCompleted = result.StepResults.Count(s => s.IsSuccess || s.WasSkipped),
                        FailedStepId = failedStep?.StepId,
                        FailedStepIndex = failedStep?.StepIndex,
                        FailureCategory = failedStep?.Error?.Category.ToString(),
                        ErrorMessage = failedStep?.Error?.Message,
                        FailedToolName = failedStep?.Error?.ToolName,
                        DurationMs = (int)result.Duration.TotalMilliseconds,
                        Timestamp = DateTimeOffset.UtcNow,
                        SessionId = request.SessionId,
                    }, ct);
                }
                catch (Exception ex)
                {
                    // The wisp's work already ran (and may have succeeded) by this point - a
                    // logging failure must not be reported back as a tool failure, or the agent
                    // may re-run side effects that already happened. Matches
                    // SpawnWispsExecutor.LogExecutionAsync's own try/catch around AppendAsync.
                    logger.LogWarning(ex, "Failed to log reflex execution record for {WispId}", result.WispId);
                }
            }

            await PublishReflexFiredAsync(skillName, filename, result, request, ct);
        }

        // One-item batch so a reflex call gets exactly the same #686 outcome reporting and
        // the same Output:-bearing response text spawn_wisps already gives every caller.
        var batch = new WispBatchResult
        {
            BatchId = $"reflex-{wispId}",
            Results = [result],
            TotalDuration = result.Duration,
        };
        SpawnWispsExecutor.ReportOutcome(outcome, batch);

        return new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = SpawnWispsExecutor.FormatBatchResult(batch),
            IsError = !result.IsSuccess,
        };
    }

    /// <summary>
    /// Best-effort publish of a <see cref="ReflexFiredEvent"/> for the observability panel
    /// (<c>design/cognitive-tiers.md</c>, "Visibility") — a bus outage here must never fail the
    /// tool call itself, matching the try/catch convention already used for <c>executionLog</c>
    /// above. No-op when either dependency wasn't wired (e.g. in tests, or a host that hasn't
    /// opted into the stimulus/visibility subsystem). <c>Trigger</c> is "stimulus" when this
    /// call originated from a <c>StimulusDispatcher</c> fire (ambient
    /// <see cref="StimulusInvocationContext"/> set), "llm" otherwise.
    /// </summary>
    private async Task PublishReflexFiredAsync(
        string skillName, string filename, WispExecutionResult result, ToolInvokeRequest request, CancellationToken ct)
    {
        if (publisher is null || agentIdentity is null)
            return;

        try
        {
            var invocation = StimulusInvocationContext.Value;
            var evt = new ReflexFiredEvent
            {
                ToolName = request.ToolName,
                SkillName = skillName,
                Filename = filename,
                Succeeded = result.IsSuccess,
                FiredAt = DateTimeOffset.UtcNow,
                DurationMs = (int)result.Duration.TotalMilliseconds,
                Trigger = invocation is null ? "llm" : "stimulus",
                SessionId = request.SessionId,
                StimulusPatternId = invocation?.PatternId,
                ErrorMessage = result.IsSuccess ? null : result.FailedStep?.Error?.Message,
                XFactor = invocation?.XFactor,
            };
            var envelope = evt.ToEnvelope<ReflexFiredEvent>(source: agentIdentity.Name);
            await publisher.PublishAsync($"{UserProxyTopics.ReflexFired}.{agentIdentity.Name}", envelope, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish reflex-fired event for {ToolName}", request.ToolName);
        }
    }

    private static ToolInvokeResponse Error(ToolInvokeRequest request, string message) =>
        new() { ToolCallId = request.ToolCallId, ToolName = request.ToolName, Content = message, IsError = true };
}
