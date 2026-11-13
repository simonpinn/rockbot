namespace RockBot.Host;

/// <summary>
/// Operator-supplied scope for an on-demand run of the wisp-success-analysis pass
/// (<see cref="IDreamCycleTrigger.TriggerWispAnalysisAsync"/>). The normal 7-day
/// lookback and <see cref="DreamOptions.WispSuccessFrequencyThreshold"/> gate still
/// apply to everything else — this only forces specific, named candidates into
/// consideration regardless of age or frequency, so an operator can fast-track
/// something they already know should become a reflex ("skills training day")
/// without disabling the normal learning process for everything alongside it.
/// A targeted candidate is never exempted from requiring every recorded run to have
/// succeeded — that gate is about correctness, not volume, and is never bypassed.
/// </summary>
public sealed record WispAnalysisTriggerRequest(
    IReadOnlyList<string>? TargetShapeHashes = null,
    string? DescriptionContains = null,
    string? OperatorHint = null)
{
    public bool HasExplicitTargets =>
        (TargetShapeHashes is { Count: > 0 }) || !string.IsNullOrWhiteSpace(DescriptionContains);

    /// <summary>True when this candidate group's hash or description was explicitly named.</summary>
    public bool MatchesTarget(string hash, string description) =>
        (TargetShapeHashes is { Count: > 0 }
            && TargetShapeHashes.Any(h => string.Equals(h, hash, StringComparison.OrdinalIgnoreCase)))
        || (!string.IsNullOrWhiteSpace(DescriptionContains)
            && description.Contains(DescriptionContains, StringComparison.OrdinalIgnoreCase));
}
