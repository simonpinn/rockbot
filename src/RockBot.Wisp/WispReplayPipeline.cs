using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RockBot.Tools;

namespace RockBot.Wisp;

/// <summary>
/// The shared canonicalize → hash → circuit-breaker-check → execute sequence every wisp
/// replay goes through, whether dispatched via <c>spawn_wisps</c> (<see cref="SpawnWispsExecutor"/>)
/// or replayed directly as a reflex tool (<c>RockBot.Tools.Reflex</c>'s <c>ReflexExecutor</c>).
/// Extracted from <see cref="SpawnWispsExecutor"/> so both callers share one implementation
/// (see <c>design/reflex-tools.md</c> Phase 2).
/// </summary>
internal static class WispReplayPipeline
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>A wisp definition, canonicalized and hashed, ready to execute.</summary>
    internal sealed record PreparedRun(
        WispDefinition Definition, string DefinitionJson, string DefinitionHash, string ShapeHash);

    /// <summary>
    /// Canonicalizes typed-name shorthand, serializes, and computes both the definition hash
    /// (exact-content match) and the shape hash (structural match, strips literal values) —
    /// the same sequence <see cref="SpawnWispsExecutor.ExecuteOneAsync"/> used inline.
    /// </summary>
    internal static PreparedRun Prepare(WispDefinition definition, IMcpToolDirectory? mcpToolDirectory)
    {
        var canonical = McpStepResolver.CanonicalizeShorthand(definition, mcpToolDirectory);
        var defJson = JsonSerializer.Serialize(canonical, JsonOptions);
        return new PreparedRun(canonical, defJson, ComputeDefinitionHash(defJson), ComputeShapeHash(canonical));
    }

    /// <summary>
    /// Runs the circuit-breaker admit check and, if allowed, executes the prepared definition.
    /// When the breaker trips, returns the canned circuit-breaker result with
    /// <c>CircuitBreakerTripped = true</c> and does <em>not</em> call <paramref name="wispExecutor"/> —
    /// callers must not log an execution record for a tripped dispatch (no work was done).
    /// </summary>
    internal static async Task<(WispExecutionResult Result, bool CircuitBreakerTripped)> RunAsync(
        WispExecutor wispExecutor,
        PreparedRun prepared,
        string wispId,
        string? parentSessionId,
        WispDispatchCircuitBreaker? circuitBreaker,
        ILogger logger,
        CancellationToken ct)
    {
        if (circuitBreaker is not null)
        {
            var decision = circuitBreaker.Admit(prepared.DefinitionHash);
            if (!decision.Allowed)
            {
                logger.LogWarning(
                    "Wisp dispatch circuit breaker tripped for definition {DefHash} ('{Description}'): " +
                    "{Count} dispatches within {WindowMinutes:F0}m (limit exceeded). Refusing to run; session={Session}.",
                    prepared.DefinitionHash, prepared.Definition.Description, decision.Count,
                    decision.Window.TotalMinutes, parentSessionId ?? "(none)");
                WispDiagnostics.CircuitBreakerTrips.Add(1);
                return (BuildCircuitBreakerResult(wispId, prepared.Definition), true);
            }
        }

        var result = await wispExecutor.ExecuteAsync(prepared.Definition, wispId, parentSessionId, ct);
        return (result, false);
    }

    internal static string ComputeDefinitionHash(string definitionJson)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(definitionJson));
        return Convert.ToHexStringLower(bytes)[..16];
    }

    /// <summary>
    /// Hash the structural shape of a wisp — gateway/server/tool/mode plus the
    /// sorted set of parameter keys per step — while stripping description,
    /// prompt text, and literal parameter values. Two wisps that differ only by
    /// description or by date/accountId values share the same shape hash, so
    /// the success-promotion pass can group repeated patterns that the LLM
    /// authored as cosmetically distinct invocations.
    /// </summary>
    internal static string ComputeShapeHash(WispDefinition definition)
    {
        var canonical = new
        {
            steps = definition.Steps.Select(s => new
            {
                id = s.Id,
                mode = s.Mode.ToString(),
                gateway = s.Gateway?.ToString(),
                server = s.Server,
                tool = s.Tool,
                language = s.Language,
                agent = s.Agent,
                skill = s.Skill,
                hasPrompt = !string.IsNullOrEmpty(s.Prompt),
                hasMessage = !string.IsNullOrEmpty(s.Message),
                paramKeys = ExtractParamKeys(s.ResolvedParams),
                metadataKeys = ExtractParamKeys(s.Metadata),
                hasInputFrom = !string.IsNullOrEmpty(s.InputFrom),
                hasOutputTo = !string.IsNullOrEmpty(s.OutputTo),
                onFailure = s.OnFailure?.Action.ToString()
            }).ToList()
        };

        var json = JsonSerializer.Serialize(canonical, JsonOptions);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexStringLower(bytes)[..16];
    }

    private static IReadOnlyList<string> ExtractParamKeys(JsonElement? element)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object)
            return [];

        var keys = new List<string>();
        foreach (var prop in element.Value.EnumerateObject())
            keys.Add(prop.Name);

        keys.Sort(StringComparer.Ordinal);
        return keys;
    }

    /// <summary>
    /// Canned result for a dispatch the circuit breaker refused. The error message is stable
    /// (no counts or ids) so identical refusals look identical to the agent-loop's
    /// repetitive-result detector, which then nudges the model to stop. Categorised
    /// <see cref="FailureCategory.External"/> — the wisp definition itself isn't malformed;
    /// it's being dispatched too aggressively.
    /// </summary>
    private static WispExecutionResult BuildCircuitBreakerResult(string wispId, WispDefinition definition) =>
        new()
        {
            WispId = wispId,
            IsSuccess = false,
            Duration = TimeSpan.Zero,
            Definition = definition,
            StepResults =
            [
                new WispStepResult
                {
                    StepId = "circuit-breaker",
                    StepIndex = 0,
                    IsSuccess = false,
                    Duration = TimeSpan.Zero,
                    Error = new WispStepError
                    {
                        Category = FailureCategory.External,
                        Message = "Circuit breaker: this exact wisp definition has been dispatched too many " +
                                  "times in a short window and is temporarily blocked. Stop re-running it — " +
                                  "fix the underlying problem or take a different approach."
                    }
                }
            ]
        };
}
