namespace RockBot.Tools.Reflex;

/// <summary>
/// Options for the reflex tool reconciliation loop. See <c>design/reflex-tools.md</c> Phase 2.
/// </summary>
public sealed class ReflexOptions
{
    /// <summary>Whether reflex tools are registered at all. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How often <c>ReflexToolCatalog</c> re-scans <see cref="RockBot.Host.ISkillStore"/> for
    /// validated Wisp resources to register or unregister. Default 60 seconds.
    /// </summary>
    public TimeSpan ReconciliationInterval { get; set; } = TimeSpan.FromSeconds(60);
}
