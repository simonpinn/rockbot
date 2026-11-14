using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Tools;

namespace RockBot.Tools.Reflex;

/// <summary>
/// Per-session Lazy/Pinned activation for reflex tools (<c>design/reflex-tools.md</c> Phase 3),
/// structurally mirroring <c>McpTypedToolSurface</c>: a bounded LRU activation list (cap 40,
/// re-use keeps a tool's place), a separate bounded pinned-skill list (cap 3, expanded into every
/// one of that skill's current reflex tools), and an idle sweep (12h) on a 10-minute timer. Does
/// not itself implement <see cref="ITypedToolSurface"/> — <c>CompositeTypedToolSurface</c> wraps
/// this alongside MCP's own surface, since the host resolves exactly one <see cref="ITypedToolSurface"/>
/// per run.
/// <para>
/// Deliberately dependency-free at construction, like <c>McpTypedToolSurface</c>, so it can be
/// taken without a DI cycle; <see cref="Bind"/> supplies its lookup once both it and
/// <c>ReflexToolCatalog</c> exist. Every member no-ops before binding.
/// </para>
/// </summary>
public sealed class ReflexToolSurface(
    IToolRegistry registry, ILogger<ReflexToolSurface> logger, TimeProvider? time = null)
    : IReflexActivationHooks
{
    private const int MaxActivatedToolsPerSession = 40;
    private const int MaxPinnedSkillsPerSession = 3;
    private static readonly TimeSpan ActivationIdleTimeout = TimeSpan.FromHours(12);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private IReflexRegistrationLookup? _lookup;
    private long _lastSweepTicks;

    /// <summary>Supplies the lookup this surface resolves tool names and skill membership against.</summary>
    internal void Bind(IReflexRegistrationLookup lookup) => _lookup = lookup;

    /// <summary>True when <paramref name="toolName"/> is a currently-registered reflex tool.</summary>
    public bool IsReflexTool(string toolName) => _lookup is { } lookup && lookup.TryGet(toolName, out _, out _);

    /// <summary>
    /// The reflex tools active for <paramref name="toolSessionId"/>: its LRU activations, and, in
    /// <see cref="TypedToolMode.Pinned"/>, every current reflex tool of each pinned skill, oldest
    /// pin first, sharing the activation budget.
    /// </summary>
    public IReadOnlyList<AIFunction> GetActivated(string toolSessionId, TypedToolMode mode)
    {
        if (_lookup is not { } lookup || mode is not (TypedToolMode.Lazy or TypedToolMode.Pinned)
            || !_sessions.TryGetValue(toolSessionId, out var session))
            return [];

        List<string> names;
        List<string> pinned;
        lock (session)
        {
            session.Touched = _time.GetUtcNow();
            names = [.. session.Names];
            pinned = mode == TypedToolMode.Pinned ? [.. session.Pinned] : [];
        }

        var functions = new List<AIFunction>(names.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (!seen.Add(name))
                continue;
            if (TryBuildFunction(name, toolSessionId) is { } fn)
                functions.Add(fn);
        }

        // Pinned skills share the activations' budget: the most recently touched skill fills it
        // first, each in no particular order within itself, following the activations in the
        // list so a new pin adds to the end and the prompt cache holds.
        var budget = Math.Max(1, MaxActivatedToolsPerSession) - functions.Count;
        var admitted = new HashSet<string>(StringComparer.Ordinal);
        for (var i = pinned.Count - 1; i >= 0 && budget > 0; i--)
        {
            foreach (var name in lookup.GetToolNames(pinned[i]))
            {
                if (budget == 0)
                    break;
                if (!seen.Contains(name) && admitted.Add(name))
                    budget--;
            }
        }

        foreach (var skillName in pinned)
        {
            foreach (var name in lookup.GetToolNames(skillName))
            {
                if (admitted.Contains(name) && seen.Add(name) && TryBuildFunction(name, toolSessionId) is { } fn)
                    functions.Add(fn);
            }
        }

        return functions;
    }

    /// <summary>
    /// The model called <paramref name="toolName"/> directly. When it is a currently-registered
    /// reflex tool, activates it for <paramref name="toolSessionId"/> and returns it; otherwise null.
    /// </summary>
    public AIFunction? ActivateByName(string toolSessionId, string toolName)
    {
        if (_lookup is not { } lookup || string.IsNullOrEmpty(toolSessionId) || !lookup.TryGet(toolName, out _, out _))
            return null;

        Activate(toolSessionId, [toolName]);
        return TryBuildFunction(toolName, toolSessionId);
    }

    /// <summary>True when <paramref name="toolName"/> is active for <paramref name="sessionId"/>.</summary>
    public bool IsActivated(string sessionId, string toolName)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return false;
        lock (session) return session.Names.Contains(toolName);
    }

    /// <summary>
    /// Pins every current reflex tool of <paramref name="skillName"/> into
    /// <paramref name="sessionId"/>'s list (the "touch" signal is skill-recall context injection
    /// surfacing the skill, not a tool call). A no-op for an unknown skill, or one with no
    /// currently-registered reflex tool, since either would only push a real pin out.
    /// </summary>
    public void Pin(string? sessionId, string skillName)
    {
        if (_lookup is not { } lookup || string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(skillName)
            || lookup.GetToolNames(skillName).Count == 0)
            return;

        var cap = Math.Max(1, MaxPinnedSkillsPerSession);
        var session = SessionFor(sessionId);
        lock (session)
        {
            session.Touched = _time.GetUtcNow();
            var index = session.Pinned.FindIndex(s => string.Equals(s, skillName, StringComparison.Ordinal));
            if (index >= 0)
                session.Pinned.RemoveAt(index);
            else
                logger.LogInformation("Reflex skill {Skill} pinned for session {Session}", skillName, sessionId);
            session.Pinned.Add(skillName);

            var overflow = session.Pinned.Count - cap;
            if (overflow > 0)
                session.Pinned.RemoveRange(0, overflow);
        }
    }

    /// <summary>The skills <paramref name="sessionId"/> has pinned, least recently touched first.</summary>
    public IReadOnlyList<string> GetPinned(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return [];
        lock (session) return [.. session.Pinned];
    }

    /// <summary>Removes <paramref name="toolNames"/> from every session's activation list.</summary>
    internal void Evict(IReadOnlySet<string> toolNames)
    {
        if (toolNames.Count == 0)
            return;

        var evicted = 0;
        foreach (var session in _sessions.Values)
        {
            lock (session)
                evicted += session.Names.RemoveAll(toolNames.Contains);
        }

        if (evicted > 0)
        {
            logger.LogInformation(
                "Evicted {Count} reflex tool activation(s) no longer validated: {Tools}",
                evicted, string.Join(", ", toolNames));
        }
    }

    private void Activate(string? sessionId, IEnumerable<string> names)
    {
        if (string.IsNullOrEmpty(sessionId))
            return;

        var requested = names.ToList();
        var cap = Math.Max(1, MaxActivatedToolsPerSession);
        var session = SessionFor(sessionId);
        lock (session)
        {
            session.Touched = _time.GetUtcNow();
            foreach (var name in requested)
            {
                // Re-activating keeps a tool's place: moving it would reorder the tool list and
                // cost the provider's prompt cache for nothing.
                if (!session.Names.Contains(name))
                    session.Names.Add(name);
            }

            var overflow = session.Names.Count - cap;
            if (overflow > 0)
                session.Names.RemoveRange(0, overflow);
        }
    }

    private AIFunction? TryBuildFunction(string toolName, string sessionId)
    {
        var registration = registry.GetTools().FirstOrDefault(t => t.Name == toolName);
        if (registration is null)
            return null;
        var executor = registry.GetExecutor(toolName);
        return executor is null ? null : new RegistryToolFunction(registration, executor, sessionId);
    }

    private Session SessionFor(string sessionId)
    {
        SweepIfDue();
        return _sessions.GetOrAdd(sessionId, _ => new Session { Touched = _time.GetUtcNow() });
    }

    private void SweepIfDue()
    {
        var now = _time.GetUtcNow();
        var last = Interlocked.Read(ref _lastSweepTicks);
        if (now.UtcTicks - last < SweepInterval.Ticks
            || Interlocked.CompareExchange(ref _lastSweepTicks, now.UtcTicks, last) != last)
            return;

        foreach (var (id, session) in _sessions)
        {
            bool idle;
            lock (session) idle = now - session.Touched > ActivationIdleTimeout;
            if (idle)
                _sessions.TryRemove(id, out _);
        }
    }

    void IReflexActivationHooks.OnSkillSurfaced(string? sessionId, string skillName) => Pin(sessionId, skillName);

    string? IReflexActivationHooks.TryGetToolName(string skillName, string filename, string? definitionHash)
    {
        if (_lookup is not { } lookup || definitionHash is null)
            return null;
        var candidate = ReflexToolNames.Build(definitionHash);
        return lookup.TryGet(candidate, out var actualSkill, out var actualFile)
            && actualSkill == skillName && actualFile == filename
            ? candidate
            : null;
    }

    private sealed class Session
    {
        public List<string> Names { get; } = [];
        public List<string> Pinned { get; } = [];
        public DateTimeOffset Touched { get; set; }
    }
}
