using System.Text.Json;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Tools;
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
    ILogger<ReflexExecutor> logger) : IToolExecutor
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

        if (!tripped && executionLog is not null)
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

    private static ToolInvokeResponse Error(ToolInvokeRequest request, string message) =>
        new() { ToolCallId = request.ToolCallId, ToolName = request.ToolName, Content = message, IsError = true };
}
