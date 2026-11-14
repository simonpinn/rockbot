using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Tools;
using RockBot.Tools.Mcp;

namespace RockBot.Tools.Reflex;

/// <summary>
/// Reconciles <see cref="IToolRegistry"/> against validated (<c>Provisional=false</c>),
/// <see cref="SkillResourceType.Wisp"/> resources across every skill, registering a reflex tool
/// per resource and unregistering one the moment it stops validating. <see cref="ISkillStore"/>
/// has no change-notification, so this is a timer-driven poll (see <c>ReflexToolRegistrar</c>),
/// not a watch. Mirrors <c>McpWrapperCatalog</c>'s diff-and-swap, immutable-snapshot pattern:
/// <see cref="TryGet"/> reads a <c>volatile</c>-swapped dictionary lock-free, so an in-flight
/// <see cref="ReflexExecutor"/> call is never invalidated by a concurrent reconciliation — it
/// completes against the snapshot it started with. See <c>design/reflex-tools.md</c> Phase 2.
/// </summary>
public sealed class ReflexToolCatalog(
    ISkillStore skillStore,
    IToolRegistry registry,
    Func<IToolExecutor> sharedExecutor,
    ILogger<ReflexToolCatalog> logger) : IReflexRegistrationLookup
{
    private readonly object _gate = new();
    private volatile IReadOnlyDictionary<string, (string SkillName, string Filename)> _byName =
        new Dictionary<string, (string SkillName, string Filename)>();
    private ReflexToolSurface? _surface;

    /// <summary>Supplies the session surface a demoted/deleted reflex tool is evicted from.</summary>
    internal void Bind(ReflexToolSurface surface) => _surface = surface;

    public async Task ReconcileOnceAsync(CancellationToken ct)
    {
        var desired = new Dictionary<string, (string SkillName, string Filename, string Description)>();
        var skills = await skillStore.ListAsync();
        foreach (var skill in skills)
        {
            foreach (var resource in skill.Manifest ?? [])
            {
                if (resource.Type != SkillResourceType.Wisp || resource.Provisional)
                    continue;

                if (resource.DefinitionHash is not { } definitionHash)
                {
                    logger.LogWarning(
                        "Reflex tool for '{Skill}/{File}' has no reflex tool: resource has no DefinitionHash. " +
                        "It stays reachable through spawn_wisps.",
                        skill.Name, resource.Filename);
                    continue;
                }

                var name = ReflexToolNames.Build(definitionHash);

                if (!McpWrapperNaming.FitsProviderLimit(name))
                {
                    logger.LogWarning(
                        "Reflex tool for '{Skill}/{File}' has no reflex tool: '{Name}' is longer than " +
                        "{MaxLength} characters. It stays reachable through spawn_wisps.",
                        skill.Name, resource.Filename, name, McpWrapperNaming.MaxLength);
                    continue;
                }

                if (desired.ContainsKey(name))
                {
                    logger.LogWarning(
                        "Reflex tool name '{Name}' is claimed by more than one skill resource this cycle " +
                        "(duplicate DefinitionHash) — keeping the first, skipping '{Skill}/{File}'",
                        name, skill.Name, resource.Filename);
                    continue;
                }

                desired[name] = (skill.Name, resource.Filename, resource.Description);
            }
        }

        List<string> stale = [];
        lock (_gate)
        {
            var current = _byName;
            var executor = sharedExecutor();

            foreach (var name in current.Keys)
            {
                if (!desired.ContainsKey(name))
                {
                    registry.Unregister(name);
                    stale.Add(name);
                }
            }

            var registered = new Dictionary<string, (string SkillName, string Filename)>();
            foreach (var (name, entry) in desired)
            {
                if (current.ContainsKey(name))
                {
                    registered[name] = (entry.SkillName, entry.Filename);
                    continue;
                }

                try
                {
                    registry.Register(
                        new ToolRegistration
                        {
                            Name = name,
                            Description = entry.Description,
                            ParametersSchema = null,
                            Source = "reflex",
                        },
                        executor);
                    registered[name] = (entry.SkillName, entry.Filename);
                }
                catch (InvalidOperationException ex)
                {
                    // Not added to `registered`: a failed registration is not retried next
                    // cycle if it were (wrongly) treated as owned, TryGet must not claim a name
                    // that was never actually registered, and a later Unregister on demotion
                    // must not remove whichever other component actually owns this name.
                    logger.LogWarning(ex, "Failed to register reflex tool '{Name}'", name);
                }
            }

            _byName = registered;
        }

        if (stale.Count > 0)
            _surface?.Evict(stale.ToHashSet());
    }

    bool IReflexRegistrationLookup.TryGet(string toolName, out string skillName, out string filename)
    {
        if (_byName.TryGetValue(toolName, out var entry))
        {
            (skillName, filename) = entry;
            return true;
        }
        skillName = "";
        filename = "";
        return false;
    }

    IReadOnlyList<string> IReflexRegistrationLookup.GetToolNames(string skillName) =>
        _byName.Where(kv => kv.Value.SkillName == skillName).Select(kv => kv.Key).ToArray();
}
