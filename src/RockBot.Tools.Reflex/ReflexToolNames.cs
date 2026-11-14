namespace RockBot.Tools.Reflex;

/// <summary>
/// Builds the registered tool name for a validated skill resource exposed as a reflex.
/// See <c>design/reflex-tools.md</c> Phase 2.
/// </summary>
public static class ReflexToolNames
{
    /// <summary>Reserved prefix identifying a reflex tool. See <c>McpServerNames</c>'s reserved-name check.</summary>
    public const string Prefix = "reflex";

    /// <summary>Separator between the prefix and the hash.</summary>
    public const string Separator = "__";

    /// <summary>
    /// Builds <c>reflex__{definitionHash}</c> from the resource's stable <c>DefinitionHash</c> —
    /// not its skill name and filename, which are mutable (a dream pass can rename a skill at
    /// any time). Anchoring to the hash means a reflex tool's name never changes across a rename
    /// and is always far under the provider length limit. Sanitizes to the same charset
    /// <see cref="RockBot.Tools.Mcp.McpWrapperNaming"/> uses — the strictest the LLM providers
    /// accept for tool names, <c>^[a-zA-Z0-9_-]{1,64}$</c> — mapping anything outside it to
    /// <c>-</c>, defensively; <c>DefinitionHash</c> is always hex in practice.
    /// </summary>
    public static string Build(string definitionHash) =>
        $"{Prefix}{Separator}{Sanitize(definitionHash)}";

    /// <summary>
    /// TEMPORARY two-argument overload, retained only until <c>ReflexToolCatalog</c> and
    /// <c>ReflexToolSurface</c> switch to <see cref="Build(string)"/> (Tasks 2 and 3 of
    /// <c>docs/superpowers/plans/2026-11-14-reflex-tool-naming.md</c>). Deleted at the end of
    /// Task 3, once nothing in this project still calls it.
    /// </summary>
    public static string Build(string skillName, string filename) =>
        $"{Prefix}{Separator}{Sanitize(skillName)}{Separator}{Sanitize(filename)}";

    private static string Sanitize(string value) =>
        value.All(IsAllowed) ? value : new string(value.Select(c => IsAllowed(c) ? c : '-').ToArray());

    private static bool IsAllowed(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '-';
}
