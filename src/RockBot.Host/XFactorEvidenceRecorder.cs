using Microsoft.Extensions.Logging;

namespace RockBot.Host;

/// <summary>
/// Turns a wavering match (<see cref="LoopDiagnostics.WaveringXFactorDetected"/>) into evidence
/// in the existing failure-cluster escalation engine — reused as-is with a synthetic key
/// (<c>Server="x-factor"</c>, mirroring <c>design/reflex-tools.md</c>'s own reuse of this store
/// for <c>Server="skill-resource"</c> clusters), so a repeated pattern of wavering already
/// escalates into a <c>RepairTicket(Target=XFactorMemory)</c> through machinery that already
/// exists — no new evidence-aggregation store. Deliberately kept outside
/// <see cref="AgentLoopRunner"/> itself, which stays a pure compute loop that never touches a
/// store directly; each of its callers (who already hold an <see cref="IFailureClusterStore"/>
/// where wired) calls this once after <c>RunAsync</c> returns.
/// See <c>design/cognitive-tiers.md</c>, "X-factor memory".
/// </summary>
public static class XFactorEvidenceRecorder
{
    public static async Task RecordIfWaveringAsync(
        LoopDiagnostics? diagnostics,
        IFailureClusterStore? failureClusterStore,
        string? sessionId,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        if (diagnostics is not { WaveringXFactorDetected: true })
            return;
        if (failureClusterStore is null)
            return;

        var recalled = diagnostics.XFactorRecalls?.Recalled ?? [];
        if (recalled.Count == 0)
            return;

        foreach (var (channel, value) in recalled)
        {
            try
            {
                var key = new ClusterKey("x-factor", channel, value);
                await failureClusterStore.RecordAsync(
                    key, sessionId, "Thought wavered on an instinct/reflex tied to this x-factor.", now, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Best-effort telemetry: a failure here must never fail the turn it's
                // recording evidence about, matching ReflexExecutor's own publish convention.
                logger?.LogWarning(ex, "Failed to record x-factor wavering evidence for '{Channel}'/'{Value}'", channel, value);
            }
        }
    }
}
