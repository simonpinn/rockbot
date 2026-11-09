using Microsoft.Extensions.AI;
using RockBot.Host;
using RockBot.Tools.Mcp;

namespace RockBot.Tools.Reflex;

/// <summary>
/// Wraps MCP's and Reflex's typed tool surfaces behind the one <see cref="ITypedToolSurface"/>
/// the host resolves per run. Reflex mirrors whichever mode <paramref name="mcp"/>'s own
/// <c>McpBridge:WrapperMode</c> config resolves to per tier — there is only one mode slot for
/// both surfaces to share, so MCP's already-configured mode is the single source of truth, not a
/// second independently-configured one that could disagree with it. <c>IsTypedTool</c>,
/// <c>GetActivated</c>, and <c>ActivateByName</c> consult both surfaces — those decide which tool
/// names apply, not which mode governs them. See <c>design/reflex-tools.md</c> Phase 3.
/// </summary>
public sealed class CompositeTypedToolSurface(McpTypedToolSurface mcp, ReflexToolSurface reflex) : ITypedToolSurface
{
    public TypedToolMode ModeFor(ModelTier tier) => mcp.ModeFor(tier);
    public string LoaderToolName => mcp.LoaderToolName;
    public string? Orientation(TypedToolMode mode) => mcp.Orientation(mode);
    public int MaxToolsPerRequest => mcp.MaxToolsPerRequest;

    // Reflex is exempt from this run's typed-tool classification in Off mode: Off never
    // re-adds anything once Shape drops a typed tool (AddActivated/TryActivate only run in
    // Lazy/Pinned), and reflex has no mcp_invoke_tool-style fallback the way MCP tools do. A
    // reflex tool must stay in the base list on an Off tier, matching Phase 2's always-available
    // behavior there, while still being Lazy/Pinned-gated on every tier that activates it.
    public bool IsTypedTool(string toolName) =>
        mcp.IsTypedTool(toolName) || (TypedToolSurfaceContext.Mode != TypedToolMode.Off && reflex.IsReflexTool(toolName));

    public IReadOnlyList<AIFunction> GetActivated(string toolSessionId, TypedToolMode mode) =>
        [.. mcp.GetActivated(toolSessionId, mode), .. reflex.GetActivated(toolSessionId, mode)];

    public AIFunction? ActivateByName(string toolSessionId, string toolName) =>
        mcp.ActivateByName(toolSessionId, toolName) ?? reflex.ActivateByName(toolSessionId, toolName);
}
