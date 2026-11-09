namespace RockBot.Host;

/// <summary>
/// Options for the x-factor memory store. When <see cref="BasePath"/> is relative it is
/// resolved under <see cref="AgentProfileOptions.BasePath"/>, mirroring
/// <see cref="FailureClusterOptions"/>.
/// </summary>
public sealed class XFactorMemoryOptions
{
    /// <summary>Base directory for x-factor memory state files. Defaults to <c>"x-factor-memory"</c>.</summary>
    public string BasePath { get; set; } = "x-factor-memory";

    /// <summary>How often in-memory state is flushed to a snapshot and the JSONL log truncated. Default 30 seconds.</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Salience weight a newly-sighted value starts at. Default 1.0.</summary>
    public double DefaultSalienceWeight { get; set; } = 1.0;

    /// <summary>
    /// Minimum salience weight a down-weight can reach. Never auto-zeroed — a record that
    /// stops mattering entirely is a human's explicit call (<c>design/cognitive-tiers.md</c>'s
    /// "explicit human-requested reset"), not something evidence-driven down-weighting reaches
    /// on its own. Default 0.05.
    /// </summary>
    public double SalienceWeightFloor { get; set; } = 0.05;

    /// <summary>
    /// How long a sighting counts as "recent" for novelty matching — the default recency
    /// window a stimulus pattern's x-factor slot uses unless it names its own. Default 24 hours.
    /// </summary>
    public TimeSpan DefaultRecencyWindow { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Minimum current salience weight for an x-factor slot to match at all, when a pattern
    /// doesn't name its own. A record down-weighted below this still exists and is still
    /// listed/surfaced — it simply stops contributing a match. Default 0.2.
    /// </summary>
    public double MatchWeightFloor { get; set; } = 0.2;

    /// <summary>Multiplicative factor a "down-weight" repair op applies by default when the ticket doesn't name its own. Default 0.5.</summary>
    public double DefaultDownWeightFactor { get; set; } = 0.5;
}
