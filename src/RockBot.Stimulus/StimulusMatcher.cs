using RockBot.Host;
using RockBot.Messaging;
using RockBot.UserProxy;

namespace RockBot.Stimulus;

/// <summary>Result of <see cref="StimulusMatcher.TryMatchAsync"/>.</summary>
internal readonly record struct StimulusMatchResult(
    bool Matched, IReadOnlyDictionary<string, string> MatchedChannels, XFactorContext? XFactor);

/// <summary>
/// Pure, deterministic matching of a <see cref="StimulusPattern"/>'s named channels against an
/// inbound <see cref="MessageEnvelope"/>. No I/O, no LLM call — satisfies
/// <c>design/cognitive-tiers.md</c>'s "deterministic code only" cost constraint directly.
/// </summary>
internal static class StimulusMatcher
{
    /// <summary>
    /// True when every channel named in <paramref name="pattern"/>.Channels resolves on
    /// <paramref name="envelope"/> and equals the pattern's required value. A pattern with no
    /// named channels matches every message on its topic. <paramref name="matchedChannels"/>
    /// carries the resolved values on a match (empty on a miss) — later needed as context
    /// for an x-factor-slotted pattern and for the observability event.
    /// </summary>
    public static bool TryMatchChannels(
        StimulusPattern pattern,
        string topic,
        MessageEnvelope envelope,
        out IReadOnlyDictionary<string, string> matchedChannels)
    {
        var resolved = new Dictionary<string, string>();

        foreach (var (channel, requiredValue) in pattern.Channels)
        {
            if (!StimulusChannels.TryResolve(channel, topic, envelope, out var value) || value != requiredValue)
            {
                matchedChannels = EmptyMatch;
                return false;
            }

            resolved[channel] = value;
        }

        matchedChannels = resolved;
        return true;
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyMatch = new Dictionary<string, string>();

    /// <summary>
    /// Full match: named channels first (cheap, synchronous — see <see cref="TryMatchChannels"/>),
    /// then, only if those all pass, the pattern's x-factor slot (if any). The x-factor check
    /// needs the store because novelty is a deviation from a persisted baseline, not a static
    /// value to compare against — see <c>design/cognitive-tiers.md</c>, "Context representation".
    /// A pattern with an x-factor slot can never match when <paramref name="xFactorStore"/> is
    /// null (not wired into this host) — there's nothing to check novelty against.
    /// </summary>
    public static async Task<StimulusMatchResult> TryMatchAsync(
        StimulusPattern pattern,
        string topic,
        MessageEnvelope envelope,
        IXFactorMemoryStore? xFactorStore,
        XFactorMemoryOptions xFactorOptions,
        DateTimeOffset now,
        CancellationToken ct)
    {
        // A miss here means the named channels themselves never resolved - nothing to preserve.
        if (!TryMatchChannels(pattern, topic, envelope, out var matchedChannels))
            return new StimulusMatchResult(false, EmptyMatch, null);

        if (pattern.XFactor is not { } slot)
            return new StimulusMatchResult(true, matchedChannels, null);

        // From here on, matchedChannels already holds real, resolved values - every near-miss
        // below preserves it rather than discarding to EmptyMatch, since the named channels
        // genuinely matched regardless of why the x-factor slot didn't.
        if (xFactorStore is null)
            return new StimulusMatchResult(false, matchedChannels, null);

        if (!StimulusChannels.TryResolve(slot.Channel, topic, envelope, out var value) || value is null)
            return new StimulusMatchResult(false, matchedChannels, null);

        var key = new XFactorMemoryKey(slot.Channel, value);
        var prior = await xFactorStore.RecordSightingAsync(key, now, ct);

        var recencyWindow = slot.RecencyWindow ?? xFactorOptions.DefaultRecencyWindow;
        var isNovel = prior is null || (now - prior.LastSeen) > recencyWindow;

        var currentWeight = prior?.SalienceWeight ?? xFactorOptions.DefaultSalienceWeight;
        var minWeight = slot.MinSalienceWeight ?? xFactorOptions.MatchWeightFloor;
        var passesWeight = currentWeight >= minWeight;

        if (!isNovel || !passesWeight)
            return new StimulusMatchResult(false, matchedChannels, null);

        return new StimulusMatchResult(true, matchedChannels, new XFactorContext(slot.Channel, value, currentWeight));
    }
}
