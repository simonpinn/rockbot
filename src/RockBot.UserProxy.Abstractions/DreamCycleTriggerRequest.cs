namespace RockBot.UserProxy;

/// <summary>
/// Asks the agent to start a full dream cycle now, outside its cron schedule.
/// See <c>RockBot.Host.IDreamCycleTrigger.TriggerFullCycleAsync</c>.
/// </summary>
public sealed record DreamCycleTriggerRequest;

/// <summary>
/// <see cref="Outcome"/> mirrors <c>RockBot.Host.DreamTriggerOutcome</c>'s names
/// ("Started", "AlreadyRunning", "Disabled") as a string rather than referencing that
/// type directly, since UserProxy.Abstractions has no project reference to RockBot.Host.
/// </summary>
public sealed record DreamCycleTriggerResponse
{
    public required string Outcome { get; init; }
}
