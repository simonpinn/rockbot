using RockBot.UserProxy;

namespace RockBot.Host;

/// <summary>
/// Context a <c>StimulusDispatcher</c> fire carries, read by <c>ReflexExecutor</c> to tell a
/// stimulus-triggered call apart from an ordinary LLM-decided one (<c>design/cognitive-tiers.md</c>
/// Phase 2 — the <c>Trigger</c> field on the reflex-fired observability event). Same
/// <see cref="AsyncLocal{T}"/>-ambient-scope shape as <see cref="ToolCallOutcomeContext"/>.
/// </summary>
public sealed record StimulusInvocation(
    string PatternId,
    string Topic,
    IReadOnlyDictionary<string, string> MatchedChannels,
    XFactorContext? XFactor = null);

/// <summary>Ambient per-async-flow <see cref="StimulusInvocation"/>. Null outside a stimulus-triggered call.</summary>
public static class StimulusInvocationContext
{
    private static readonly AsyncLocal<StimulusInvocation?> Current = new();

    public static StimulusInvocation? Value => Current.Value;

    /// <summary>Binds <paramref name="invocation"/> to the current async flow until the scope is disposed.</summary>
    public static IDisposable Set(StimulusInvocation? invocation)
    {
        var previous = Current.Value;
        Current.Value = invocation;
        return new Scope(previous);
    }

    private sealed class Scope(StimulusInvocation? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
