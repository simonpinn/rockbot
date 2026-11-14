namespace RockBot.Host;

/// <summary>
/// Optional hook from skill-recall context injection into reflex tool activation
/// (<c>design/reflex-tools.md</c> Phase 3). Resolved as a nullable collaborator — null whenever
/// reflex tools aren't installed — so <c>RockBot.Host</c> never takes a project reference on
/// <c>RockBot.Tools.Reflex</c>.
/// </summary>
public interface IReflexActivationHooks
{
    /// <summary>
    /// A skill recalled this turn: pins every current reflex tool of <paramref name="skillName"/>
    /// into <paramref name="sessionId"/>'s activation list.
    /// </summary>
    void OnSkillSurfaced(string? sessionId, string skillName);

    /// <summary>
    /// The currently-registered reflex tool name for the resource at
    /// <paramref name="skillName"/>/<paramref name="filename"/> with the given
    /// <paramref name="definitionHash"/>, or null if none is registered or
    /// <paramref name="definitionHash"/> is null.
    /// </summary>
    string? TryGetToolName(string skillName, string filename, string? definitionHash);
}
