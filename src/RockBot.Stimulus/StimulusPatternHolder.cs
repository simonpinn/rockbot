namespace RockBot.Stimulus;

/// <summary>
/// Holds the currently-loaded set of <see cref="StimulusPattern"/>s and tracks a version
/// counter that increments on each update — the same volatile-snapshot-plus-version-counter
/// shape as <c>RockBot.Host.ProfileHolder</c>. Unlike <c>ProfileHolder</c>, this also raises
/// <see cref="OnUpdated"/>, because <see cref="StimulusDispatcher"/> (unlike anything that
/// reads a profile) must react to a reload by re-subscribing/unsubscribing bus topics, not
/// just read a newer snapshot on its next access.
/// </summary>
public sealed class StimulusPatternHolder
{
    private volatile IReadOnlyList<StimulusPattern> _patterns = [];
    private long _version;

    /// <summary>Current version. Increments on each <see cref="Update"/> call.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>The currently-loaded patterns. Empty (never null) until the first load.</summary>
    public IReadOnlyList<StimulusPattern> Patterns => _patterns;

    /// <summary>Raised after <see cref="Update"/> replaces the pattern set.</summary>
    public event Action? OnUpdated;

    /// <summary>Atomically replaces the pattern set, increments the version, and raises <see cref="OnUpdated"/>.</summary>
    public void Update(IReadOnlyList<StimulusPattern> patterns)
    {
        _patterns = patterns ?? throw new ArgumentNullException(nameof(patterns));
        Interlocked.Increment(ref _version);
        OnUpdated?.Invoke();
    }
}
