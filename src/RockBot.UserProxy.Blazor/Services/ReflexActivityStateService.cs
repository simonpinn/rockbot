using RockBot.UserProxy;

namespace RockBot.UserProxy.Blazor.Services;

/// <summary>
/// Holds a bounded, newest-first feed of <see cref="ReflexFiredEvent"/>s, independent of any
/// chat session — "a view onto what is effectively a background consciousness"
/// (<c>design/cognitive-tiers.md</c>, "Visibility"). Deliberately not folded into
/// <see cref="ChatStateService"/>: that service's state is per-turn and per-session; a
/// stimulus-triggered fire has neither.
/// </summary>
public sealed class ReflexActivityStateService(int capacity = 200)
{
    private readonly List<ReflexFiredEvent> _events = [];
    private readonly object _lock = new();

    public event Action? OnStateChanged;

    /// <summary>Newest-first snapshot of the feed.</summary>
    public IReadOnlyList<ReflexFiredEvent> Events
    {
        get { lock (_lock) return _events.ToList(); }
    }

    public void Append(ReflexFiredEvent evt)
    {
        lock (_lock)
        {
            _events.Insert(0, evt);
            if (_events.Count > capacity)
                _events.RemoveRange(capacity, _events.Count - capacity);
        }
        OnStateChanged?.Invoke();
    }
}
