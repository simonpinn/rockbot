namespace RockBot.UserProxy;

/// <summary>
/// Published every time a reflex tool executes — whether an LLM decided to call it by name or
/// a <c>StimulusDispatcher</c> fired it directly off the bus with no LLM involved at all. Has no
/// owning chat session when <see cref="Trigger"/> is <c>"stimulus"</c>, which is why it rides its
/// own topic (<see cref="UserProxyTopics.ReflexFired"/>) rather than <see cref="AgentReply"/>'s
/// per-session reply stream. See <c>design/cognitive-tiers.md</c>, "Visibility".
/// </summary>
public sealed record ReflexFiredEvent
{
    public required string ToolName { get; init; }
    public required string SkillName { get; init; }
    public required string Filename { get; init; }
    public required bool Succeeded { get; init; }
    public required DateTimeOffset FiredAt { get; init; }
    public required int DurationMs { get; init; }

    /// <summary>"llm" when an agent turn decided to call this reflex by name; "stimulus" when a <c>StimulusDispatcher</c> fired it with no LLM in the loop.</summary>
    public required string Trigger { get; init; }

    /// <summary>Null for a stimulus-triggered fire — there is no owning chat session.</summary>
    public string? SessionId { get; init; }

    /// <summary>The stimulus pattern id that fired this call, when <see cref="Trigger"/> is <c>"stimulus"</c>.</summary>
    public string? StimulusPatternId { get; init; }

    public string? ErrorMessage { get; init; }

    /// <summary>Populated when the firing stimulus pattern carried an x-factor slot; null otherwise.</summary>
    public XFactorContext? XFactor { get; init; }
}
