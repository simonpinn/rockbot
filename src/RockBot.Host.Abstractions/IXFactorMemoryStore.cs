namespace RockBot.Host;

/// <summary>
/// Persisted, per-(channel, value) sighting history backing a stimulus pattern's x-factor
/// slot. See <c>design/cognitive-tiers.md</c>, "X-factor memory": long-lived by default,
/// revisable only through <see cref="AdjustSalienceAsync"/> (evidence-driven down-weighting
/// via the repair loop, or an explicit human reset) — never a passive decay-over-time.
/// </summary>
public interface IXFactorMemoryStore
{
    /// <summary>
    /// Records a sighting of <paramref name="key"/> at <paramref name="at"/>, creating the
    /// record at <see cref="XFactorMemoryOptions.DefaultSalienceWeight"/> on first sight.
    /// Returns the record as it stood <em>before</em> this sighting was applied (null on a
    /// first sighting) — a caller judging novelty needs the prior <c>LastSeen</c>, not the one
    /// this call is about to set.
    /// </summary>
    Task<XFactorMemoryRecord?> RecordSightingAsync(
        XFactorMemoryKey key, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>Returns the current record for <paramref name="key"/>, or null if it has never been sighted.</summary>
    Task<XFactorMemoryRecord?> TryGetAsync(XFactorMemoryKey key, CancellationToken cancellationToken = default);

    /// <summary>Returns every tracked record, ordered by <see cref="XFactorMemoryRecord.LastSeen"/> descending.</summary>
    Task<IReadOnlyList<XFactorMemoryRecord>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets <paramref name="key"/>'s current salience weight directly — the one mutation path
    /// for revising a record's influence, used by the repair loop's down-weight applier and any
    /// future explicit human-reset action. Returns the record as it stood <em>before</em> the
    /// adjustment (null if the key has never been sighted), so a caller like a repair applier
    /// can revert to it exactly on a failed verify.
    /// </summary>
    Task<XFactorMemoryRecord?> AdjustSalienceAsync(
        XFactorMemoryKey key, double newWeight, CancellationToken cancellationToken = default);
}
