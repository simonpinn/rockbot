namespace RockBot.UserProxy;

/// <summary>
/// Asks the agent to run the wisp-success-analysis and provisional-validation passes
/// now, scoped by the given operator-named targets. See
/// <c>RockBot.Host.IDreamCycleTrigger.TriggerWispAnalysisAsync</c> and
/// <c>RockBot.Host.WispAnalysisTriggerRequest</c>, which this mirrors field-for-field
/// (UserProxy.Abstractions has no project reference to RockBot.Host — named with a
/// "Message" suffix so a handler that uses both types side by side never needs a
/// fully-qualified name to disambiguate them).
/// </summary>
public sealed record WispAnalysisTriggerRequestMessage
{
    public IReadOnlyList<string>? TargetShapeHashes { get; init; }
    public string? DescriptionContains { get; init; }
    public string? OperatorHint { get; init; }
}

/// <summary>Mirrors <c>RockBot.Host.WispAnalysisTriggerOutcome</c> field-for-field.</summary>
public sealed record WispAnalysisTriggerResponseMessage
{
    public required bool Ran { get; init; }
    public string? SkippedReason { get; init; }
    public int CandidatesAttached { get; init; }
    public int ProvisionalPromoted { get; init; }
    public int ProvisionalFlaggedForRepair { get; init; }
    public int ProvisionalRemoved { get; init; }
    public int ProvisionalMarkedStale { get; init; }
    public int ProvisionalConsidered { get; init; }
}
