namespace RockBot.Host;

/// <summary>
/// On-demand entry points into <c>DreamService</c>, which is internal — exposed so a
/// UI command (e.g. a Blazor control) can ask for a cycle to run now instead of
/// waiting for the next cron slot. <c>DreamService</c> is registered under this
/// interface alongside its existing singleton/<see cref="Microsoft.Extensions.Hosting.IHostedService"/>
/// registrations; see <c>AgentMemoryExtensions.WithDreamService</c>.
/// </summary>
public interface IDreamCycleTrigger
{
    /// <summary>
    /// Starts a full dream cycle (every pass — episode extraction, memory mining,
    /// graph consolidation, wisp analysis, identity reflection, and so on) right now,
    /// outside the cron schedule. Fire-and-forget: this returns as soon as it knows
    /// whether the cycle started, not when it finishes — a full cycle commonly takes
    /// tens of minutes. The existing cron schedule is untouched; this is an extra,
    /// one-off run alongside it.
    /// </summary>
    Task<DreamTriggerOutcome> TriggerFullCycleAsync(CancellationToken ct = default);

    /// <summary>
    /// Runs only the wisp-success-analysis pass (optionally scoped by
    /// <paramref name="request"/>) followed by the provisional-validation pass, then
    /// returns what happened — fast, awaited, for iterating on a promotion-pipeline
    /// fix without paying for a full cycle's other ~15 unrelated passes. Defers,
    /// same as the cron cycle, when the agent is busy with a live conversation.
    /// </summary>
    Task<WispAnalysisTriggerOutcome> TriggerWispAnalysisAsync(
        WispAnalysisTriggerRequest? request = null, CancellationToken ct = default);
}

public enum DreamTriggerOutcome
{
    /// <summary>The cycle was started. No guarantee it will avoid deferring internally if the agent turns out busy — see the logs.</summary>
    Started,

    /// <summary>A cycle (cron-scheduled or previously triggered) is already in flight.</summary>
    AlreadyRunning,

    /// <summary>Dreaming is disabled (<c>DreamOptions.Enabled</c> is false).</summary>
    Disabled,
}

/// <summary>Result of an on-demand wisp-analysis trigger — see <see cref="IDreamCycleTrigger.TriggerWispAnalysisAsync"/>.</summary>
public sealed record WispAnalysisTriggerOutcome(
    bool Ran,
    string? SkippedReason,
    int CandidatesAttached = 0,
    int ProvisionalPromoted = 0,
    int ProvisionalFlaggedForRepair = 0,
    int ProvisionalRemoved = 0,
    int ProvisionalMarkedStale = 0,
    int ProvisionalConsidered = 0);
