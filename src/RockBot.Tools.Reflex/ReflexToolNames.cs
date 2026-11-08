namespace RockBot.Tools.Reflex;

/// <summary>
/// Builds the registered tool name for a validated skill resource exposed as a reflex.
/// See <c>design/reflex-tools.md</c> Phase 2.
/// </summary>
public static class ReflexToolNames
{
    /// <summary>Reserved prefix identifying a reflex tool. See <c>McpServerNames</c>'s reserved-name check.</summary>
    public const string Prefix = "reflex";

    /// <summary>Separator between the prefix, skill name, and filename.</summary>
    public const string Separator = "__";

    /// <summary>
    /// Builds <c>reflex__{skill}__{filename}</c>, sanitizing each part independently to the same
    /// charset <see cref="RockBot.Tools.Mcp.McpWrapperNaming"/> uses — the strictest the LLM
    /// providers accept for tool names, <c>^[a-zA-Z0-9_-]{1,64}$</c> — mapping anything outside it
    /// (including <c>.</c>, which every skill resource filename has, and <c>/</c>, which skill
    /// names may have) to <c>-</c>. Never truncates — a name over the provider length limit is the
    /// caller's job to detect (<see cref="RockBot.Tools.Mcp.McpWrapperNaming.FitsProviderLimit"/>)
    /// and skip registering, the same way <c>McpWrapperCatalog</c> already does for MCP tools.
    /// </summary>
    public static string Build(string skillName, string filename) =>
        $"{Prefix}{Separator}{Sanitize(skillName)}{Separator}{Sanitize(filename)}";

    private static string Sanitize(string value) =>
        value.All(IsAllowed) ? value : new string(value.Select(c => IsAllowed(c) ? c : '-').ToArray());

    private static bool IsAllowed(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '-';
}
