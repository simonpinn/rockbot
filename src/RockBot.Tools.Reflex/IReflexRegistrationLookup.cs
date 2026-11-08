namespace RockBot.Tools.Reflex;

/// <summary>
/// Resolves a registered reflex tool name back to the skill resource it replays.
/// Implemented by <c>ReflexToolCatalog</c>, consumed by <see cref="ReflexExecutor"/> so the
/// two can be tested independently (see <c>design/reflex-tools.md</c> Phase 2).
/// </summary>
internal interface IReflexRegistrationLookup
{
    bool TryGet(string toolName, out string skillName, out string filename);
}
