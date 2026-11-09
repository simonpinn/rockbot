namespace RockBot.Host;

/// <summary>
/// Persisted record of an x-factor sighting history — the "has this been seen before, and how
/// much weight does it currently carry" memory a stimulus pattern's x-factor slot consults.
/// Long-lived by default: nothing here decays automatically on a timer. <see cref="SalienceWeight"/>
/// only ever changes through a deliberate act — evidence-driven down-weighting via the repair
/// loop, or an explicit human-requested reset — never passive time-based decay.
/// See <c>design/cognitive-tiers.md</c>, "X-factor memory".
/// </summary>
/// <param name="Key">Channel + value identity.</param>
/// <param name="Count">Total number of times this value has been sighted in this channel.</param>
/// <param name="FirstSeen">UTC timestamp of the first sighting.</param>
/// <param name="LastSeen">UTC timestamp of the most recent sighting.</param>
/// <param name="SalienceWeight">Current weight. Starts at <see cref="XFactorMemoryOptions.DefaultSalienceWeight"/>, never auto-zeroed — floored at <see cref="XFactorMemoryOptions.SalienceWeightFloor"/>.</param>
public sealed record XFactorMemoryRecord(
    XFactorMemoryKey Key,
    int Count,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    double SalienceWeight);
