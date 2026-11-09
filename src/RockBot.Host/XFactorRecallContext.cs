namespace RockBot.Host;

/// <summary>
/// Tracks every <c>(channel, value)</c> a <c>recall_x_factor_memory</c> call surfaced during the
/// current turn, so a later detector can attribute wavering evidence to a specific x-factor
/// (<c>design/cognitive-tiers.md</c> Phase 5). Not thread-contended in practice (one tool call
/// at a time per turn) but locked defensively since <see cref="AgentLoopRunner"/> runs tool calls
/// that could, in principle, run concurrently within one turn.
/// </summary>
public sealed class XFactorRecallLedger
{
    private readonly List<(string Channel, string Value)> _recalled = [];
    private readonly object _lock = new();

    public void Add(string channel, string value)
    {
        lock (_lock) _recalled.Add((channel, value));
    }

    public IReadOnlyList<(string Channel, string Value)> Recalled
    {
        get { lock (_lock) return _recalled.ToList(); }
    }
}

/// <summary>Ambient per-async-flow <see cref="XFactorRecallLedger"/>. Same <see cref="AsyncLocal{T}"/> shape as <see cref="ToolCallOutcomeContext"/>.</summary>
public static class XFactorRecallContext
{
    private static readonly AsyncLocal<XFactorRecallLedger?> Current = new();

    public static XFactorRecallLedger? Value => Current.Value;

    public static IDisposable Set(XFactorRecallLedger? ledger)
    {
        var previous = Current.Value;
        Current.Value = ledger;
        return new Scope(previous);
    }

    private sealed class Scope(XFactorRecallLedger? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
