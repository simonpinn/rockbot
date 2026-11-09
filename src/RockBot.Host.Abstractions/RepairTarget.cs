namespace RockBot.Host;

/// <summary>
/// What a <see cref="RepairTicket"/> mutates when applied. Each target has a
/// matching <see cref="IRepairTargetApplier"/> implementation that interprets
/// the ticket's <c>Change</c> payload. See <c>design/self-repair.md</c> Phase 4.
/// </summary>
public enum RepairTarget
{
    /// <summary>Edit a named skill's body — append, replace section, or delete section.</summary>
    SkillBody,

    /// <summary>Delete working-memory entries by key or key-prefix.</summary>
    WorkingMemoryEvict,

    /// <summary>Append a default value to <c>/data/agent/tool-defaults/{server}.json</c>.</summary>
    ToolDefaultRegister,

    /// <summary>Append or replace a hint section in <c>/data/agent/prompt-hints/{category}.md</c>.</summary>
    PromptBuilderHint,

    /// <summary>
    /// Attach, demote, or remove a sub-resource on a named skill. Self-repair attaches
    /// always land provisional; the validation pass promotes them to non-provisional
    /// after observed repeated success.
    /// </summary>
    SkillResource,

    /// <summary>
    /// Down-weight an x-factor memory's current salience after evidence that it's become a
    /// blocker rather than a useful signal. See <c>design/cognitive-tiers.md</c>, "X-factor
    /// memory" — revision is always evidence-driven through this path (or an explicit human
    /// reset), never a passive decay-over-time.
    /// </summary>
    XFactorMemory,
}
