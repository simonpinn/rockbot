namespace RockBot.Stimulus;

/// <summary>
/// A human-authored, PR-reviewed trigger: when a bus message on <see cref="Topic"/> matches
/// every channel named in <see cref="Channels"/>, <see cref="ReflexTool"/> is called directly,
/// with no LLM involved. Deserialized from a single JSON file under the stimuli config
/// directory — see <c>design/cognitive-tiers.md</c>, "Who defines a stimulus pattern": this is
/// new logic deciding when thought gets bypassed entirely, so it is authored as a checked-in
/// file reviewed like any other code change, never written by the agent itself at runtime.
/// </summary>
public sealed record StimulusPattern
{
    /// <summary>Stable identifier, unique across the stimuli directory. Also the file's basename by convention.</summary>
    public required string Id { get; init; }

    public string Description { get; init; } = "";

    /// <summary>When false, this pattern is loaded but never matched or subscribed for.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The bus topic this pattern's channels are evaluated against.</summary>
    public required string Topic { get; init; }

    /// <summary>
    /// Channel name to required value. Every entry must resolve and match for the pattern to
    /// fire; channels not named here are never inspected. See <see cref="StimulusChannels"/>
    /// for the channel-name vocabulary.
    /// </summary>
    public IReadOnlyDictionary<string, string> Channels { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// The reflex tool to invoke on a match. Must start with <see cref="ReflexToolPrefix"/> —
    /// the dispatcher can structurally never fire anything but a validated reflex.
    /// </summary>
    public required string ReflexTool { get; init; }

    /// <summary>Minimum seconds between fires of this specific pattern. Null means no throttle.</summary>
    public double? MinIntervalSeconds { get; init; }

    /// <summary>
    /// The pattern's one extra, often-novel signal — at most one per pattern, matching
    /// <c>design/cognitive-tiers.md</c>'s sparse-channel model. Null means the pattern matches
    /// on named channels alone, with no novelty requirement.
    /// </summary>
    public StimulusXFactorSlot? XFactor { get; init; }

    /// <summary>Prefix every <see cref="ReflexTool"/> value must start with.</summary>
    public const string ReflexToolPrefix = "reflex__";

    /// <summary>True when this pattern is structurally safe to dispatch: enabled and targets an actual reflex tool.</summary>
    public bool IsValid => ReflexTool.StartsWith(ReflexToolPrefix, StringComparison.Ordinal);
}

/// <summary>
/// A stimulus pattern's x-factor slot: the channel to check for novelty, and optional
/// overrides for the store-wide recency window / minimum salience weight defaults
/// (<c>RockBot.Host.XFactorMemoryOptions</c>).
/// </summary>
public sealed record StimulusXFactorSlot
{
    public required string Channel { get; init; }
    public TimeSpan? RecencyWindow { get; init; }
    public double? MinSalienceWeight { get; init; }
}
