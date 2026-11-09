# Reflex Tools Phase 3: `ReflexToolSurface` (lazy activation, pin-the-owning-skill) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give reflex tools (Phase 2, always-eager today) real Lazy/Pinned activation, riding the same `ITypedToolSurface` mechanism MCP's typed `{server}__{tool}` tools already use in production (Pinned mode, confirmed live via `AddMcpToolProxy`), without changing MCP's own behavior or config.

**Architecture:** A new `ReflexToolSurface` (per-session LRU activation list, cap 40; per-session pinned-skill list, cap 3; idle-swept after 12h) mirrors `McpTypedToolSurface`'s structure exactly but stays its own class — not a shared internals merge. Since the host resolves exactly one `ITypedToolSurface` per run (`TypedToolSurfaceContext`'s single `AsyncLocal` slot), a new `CompositeTypedToolSurface` wraps both `McpTypedToolSurface` and `ReflexToolSurface`, replacing the direct binding `AddMcpToolProxy` installs today. Reflex mirrors whichever mode MCP's own `McpBridge:WrapperMode` config resolves to per tier (Pinned, today) rather than carrying a second, independently-configured mode that could disagree with it over the one slot both surfaces share. The "touch" that pins a skill's reflexes is skill-recall context injection in `AgentContextBuilder` (not a tool call — reflex has no `mcp_find_tools`-equivalent loader tool), wired through a small new `IReflexActivationHooks` interface in `RockBot.Host` so that project never takes a hard dependency on `RockBot.Tools.Reflex`. `ReflexToolCatalog` and `ReflexToolSurface` resolve their mutual reference the same way `McpWrapperCatalog`/`McpTypedToolSurface` already do — each built independently, then bound to each other once both exist (`internal void Bind(...)`), avoiding a DI construction cycle.

**Tech Stack:** .NET 10, MSTest, Rocks-free hand-written fakes (matching this codebase's existing convention in `RockBot.Tools.Reflex.Tests`), `Microsoft.Extensions.AI` (`AIFunction`), `Microsoft.Extensions.DependencyInjection`.

**Spec:** `design/reflex-tools.md` (Phase 3 section, as corrected during this plan's own pre-flight research — see that section's "Change" for the full record of what the original sketch got wrong and why).

## Global Constraints

- Reflex tools ship zero-argument today (Phase 2); nothing here changes that.
- `ReflexToolSurface`'s caps mirror `McpToolSurfaceOptions`' already-shipped defaults exactly: activation cap 40 (`MaxActivatedToolsPerSession`, `src/RockBot.Tools.Mcp/McpToolSurfaceOptions.cs:67`), pin cap 3 (`MaxPinnedServersPerSession`, same file:53), idle timeout 12h (`ActivationIdleTimeout`, same file:72), sweep interval 10 minutes (matching `McpTypedToolSurface`'s own `SweepInterval` constant). These are hardcoded constants in `ReflexToolSurface`, not a new `IOptions<T>` — the design doesn't ask for independent configurability of these specific values, and `McpToolSurfaceOptions` already proves the values at this scale; introducing a second options type to hold the same three numbers would be YAGNI.
- `ReflexToolSurface` is **dependency-free at construction** (no `IReflexRegistrationLookup` constructor parameter) — mirroring `McpTypedToolSurface`'s own documented reason ("so the agent loop can take it without a DI cycle"). It is bound to its lookup via `internal void Bind(IReflexRegistrationLookup lookup)`, called once from `ReflexServiceCollectionExtensions.AddReflexTools`'s factory, after both singletons exist. Every method guards on `_lookup is not { } lookup` and no-ops/returns empty/null before binding — same pattern as `McpTypedToolSurface`'s `_catalog is { } catalog` guards.
- `ReflexToolCatalog`'s existing public constructor signature (`ISkillStore, IToolRegistry, Func<IToolExecutor>, ILogger<ReflexToolCatalog>`) does **not** change in this plan — no existing call site (`ReflexToolCatalogTests.NewCatalog`, the DI factory) needs updating. The new eviction wiring is a second `internal void Bind(ReflexToolSurface surface)` method and a nullable `_surface` field, called the same way.
- `RockBot.Host` never takes a project reference on `RockBot.Tools.Reflex` or `RockBot.Tools.Mcp`. All reflex-aware behavior in `AgentContextBuilder.cs` goes through the new `IReflexActivationHooks` interface (defined in `RockBot.Host`), resolved as an optional (`IReflexActivationHooks?`) constructor parameter that is `null` whenever reflex tools aren't installed — same convention as `ReflexExecutor`'s own `IWispExecutionLog?`/`WispDispatchCircuitBreaker?`/`IMcpToolDirectory?` parameters from Phase 2.
- `CompositeTypedToolSurface` takes `McpTypedToolSurface` and `ReflexToolSurface` as required (non-nullable) constructor parameters — both are confirmed-live, unconditionally-registered singletons in the real deployed agent (`AddMcpToolProxy` at `Program.cs:385`, `AddReflexTools` at `Program.cs:399`), so there is no "MCP not installed" case to degrade for in this codebase as it exists today.
- `dotnet build RockBot.slnx` and `dotnet test RockBot.slnx` (unit only) must pass after every task; this phase touches no integration-only (RabbitMQ) surface, so the RabbitMQ-gated run is not required before any task, matching Phase 2's own verification scope.

## Review Focus

- **The optional collaborator is absent.** `AgentContextBuilder` built with `reflexHooks: null` (today's default until `RockBot.Tools.Reflex` is referenced, and the realistic state for any other host that doesn't install reflex tools) must render the skill index and perform skill recall exactly as before Phase 3 — not just "without throwing." Pinned to Task 4's `SkillIndex_NoReflexHooks_RendersUnchanged`.
- **A tool name neither inner surface recognizes.** The model hallucinates or recalls a stale tool name that is neither an MCP typed tool nor a reflex tool. `CompositeTypedToolSurface.ActivateByName`/`IsTypedTool` must return null/false cleanly, not throw. Pinned to Task 5's `ActivateByName_UnknownToEither_ReturnsNull`.
- **Re-touching the same skill.** A session that keeps working in one skill's area recalls it on every turn. Re-pinning an already-pinned skill must refresh its recency (so it survives the pin cap) without duplicating it in the pinned list or in `GetActivated`'s output. Pinned to Task 2's `Pin_SameSkillTwice_RefreshesRecencyWithoutDuplicating`.
- **A pinned skill loses its reflexes mid-session.** A reconciliation cycle demotes every validated resource in a skill a session has pinned. `GetActivated(session, Pinned)` must stop returning that skill's tool names on the very next call — self-healing via a live re-query, not stale cached names. Pinned to Task 2's `GetActivated_Pinned_SkillLosesAllReflexTools_StopsAppearingForThatSkill`.
- **A null/empty session id reaches the surface.** Some code path (a worker run, a malformed request) calls `Pin`/`OnSkillSurfaced`/`Activate` with no real session id. Must no-op, not throw or create a garbage session entry. Pinned to Task 2's `Pin_NullOrEmptySessionId_NoOp` and `Activate_NullOrEmptySessionId_NoOp`.

---

### Task 1: `IReflexRegistrationLookup.GetToolNames` — reverse lookup by skill

**Files:**
- Modify: `src/RockBot.Tools.Reflex/IReflexRegistrationLookup.cs`
- Modify: `src/RockBot.Tools.Reflex/ReflexToolCatalog.cs`
- Modify: `tests/RockBot.Tools.Reflex.Tests/ReflexToolCatalogTests.cs`
- Modify: `tests/RockBot.Tools.Reflex.Tests/ReflexExecutorTests.cs` (its `FakeReflexRegistrationLookup` must keep implementing the interface)

**Interfaces:**
- Produces: `IReflexRegistrationLookup.GetToolNames(string skillName) : IReadOnlyList<string>` — every currently-registered reflex tool name belonging to `skillName`, order unspecified. Consumed by Task 2 (`ReflexToolSurface.Pin`/`GetActivated`).

- [ ] **Step 1: Write the failing test**

In `ReflexToolCatalogTests.cs`:

```csharp
[TestMethod]
public async Task GetToolNames_ReturnsOnlyToolsRegisteredForThatSkill()
{
    var skillStore = new FakeSkillStore
    {
        Skills =
        [
            new Skill("calendar/scan", "s", "c", DateTimeOffset.UtcNow, Manifest:
                [new SkillResource("fanout.json", SkillResourceType.Wisp, "d", Provisional: false)]),
            new Skill("other/skill", "s", "c", DateTimeOffset.UtcNow, Manifest:
                [new SkillResource("x.json", SkillResourceType.Wisp, "d", Provisional: false)]),
        ],
    };
    var registry = new FakeToolRegistry();
    var catalog = NewCatalog(skillStore, registry);
    await catalog.ReconcileOnceAsync(CancellationToken.None);

    var lookup = (IReflexRegistrationLookup)catalog;
    var expected = ReflexToolNames.Build("calendar/scan", "fanout.json");
    CollectionAssert.AreEqual(new[] { expected }, lookup.GetToolNames("calendar/scan").ToArray());
    Assert.AreEqual(0, lookup.GetToolNames("no-such-skill").Count);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test RockBot.slnx --filter "FullyQualifiedName~GetToolNames_ReturnsOnlyToolsRegisteredForThatSkill"`
Expected: build error — `IReflexRegistrationLookup` has no member `GetToolNames`.

- [ ] **Step 3: Add the member and implement it**

In `IReflexRegistrationLookup.cs`, add to the interface:

```csharp
IReadOnlyList<string> GetToolNames(string skillName);
```

In `ReflexToolCatalog.cs`, implement it next to the existing explicit `TryGet` implementation:

```csharp
IReadOnlyList<string> IReflexRegistrationLookup.GetToolNames(string skillName) =>
    _byName.Where(kv => kv.Value.SkillName == skillName).Select(kv => kv.Key).ToArray();
```

In `ReflexExecutorTests.cs`, add the same member to `FakeReflexRegistrationLookup` (whatever minimal implementation keeps it compiling — e.g. backed by the same dictionary that fake already uses for `TryGet`, or an empty array if that fake has no such backing store; it is not exercised by any existing test in that file).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test RockBot.slnx --filter "FullyQualifiedName~GetToolNames_ReturnsOnlyToolsRegisteredForThatSkill"`
Expected: PASS.

- [ ] **Step 5: Run the full Reflex test assemblies to confirm no regression**

Run: `dotnet test RockBot.slnx --filter "FullyQualifiedName~RockBot.Tools.Reflex"`
Expected: all pass (this interface has exactly one real and one fake implementer; both now compile).

- [ ] **Step 6: Commit**

```bash
git add src/RockBot.Tools.Reflex/IReflexRegistrationLookup.cs src/RockBot.Tools.Reflex/ReflexToolCatalog.cs tests/RockBot.Tools.Reflex.Tests/ReflexToolCatalogTests.cs tests/RockBot.Tools.Reflex.Tests/ReflexExecutorTests.cs
git commit -m "feat: reflex catalog can list a skill's registered tool names"
```

---

### Task 2: `ReflexToolSurface` — per-session LRU activation and pin-the-owning-skill

**Files:**
- Create: `src/RockBot.Tools.Reflex/ReflexToolSurface.cs`
- Create: `tests/RockBot.Tools.Reflex.Tests/ReflexToolSurfaceTests.cs`
- Modify: `src/RockBot.Tools.Reflex/RockBot.Tools.Reflex.csproj` (add a project reference so `RegistryToolFunction` is reachable directly rather than only transitively through the existing `RockBot.Tools.Mcp` reference)

**Interfaces:**
- Consumes: `IReflexRegistrationLookup.TryGet`/`GetToolNames` (Task 1); `IToolRegistry.GetTools()`/`GetExecutor(string)` (`RockBot.Tools.Abstractions`, existing); `RegistryToolFunction(ToolRegistration registration, IToolExecutor executor, string? sessionId, string? batchId = null, Action<string>? onInvoke = null) : AIFunction, ISessionBoundTool` (`RockBot.Tools`, existing public type — `src/RockBot.Tools/RegistryToolFunction.cs`).
- Produces (consumed by Task 3, 4, 5):

```csharp
public sealed class ReflexToolSurface(
    IToolRegistry registry, ILogger<ReflexToolSurface> logger, TimeProvider? time = null)
{
    internal void Bind(IReflexRegistrationLookup lookup);
    public bool IsReflexTool(string toolName);
    public IReadOnlyList<AIFunction> GetActivated(string toolSessionId, TypedToolMode mode);
    public AIFunction? ActivateByName(string toolSessionId, string toolName);
    public void Pin(string? sessionId, string skillName);
    public IReadOnlyList<string> GetPinned(string sessionId);
    public bool IsActivated(string sessionId, string toolName);
    internal void Evict(IReadOnlySet<string> toolNames);
}
```

Every member guards on `_lookup is not { } lookup` (unbound) exactly as `McpTypedToolSurface` guards on `_catalog is { } catalog` — no-op / `false` / `[]` / `null` before `Bind` has run.

- [ ] **Step 1: Add the project reference**

In `RockBot.Tools.Reflex.csproj`, add alongside the existing `ProjectReference` items:

```xml
<ProjectReference Include="..\RockBot.Tools\RockBot.Tools.csproj" />
```

(`RockBot.Tools.Reflex` already reaches `RockBot.Tools` transitively through its existing `RockBot.Tools.Mcp` reference — `RockBot.Tools.Mcp.csproj` itself references `RockBot.Tools` — so this compiles today without the change; the direct reference makes that dependency explicit instead of relying on a transitive path that could silently break if `RockBot.Tools.Mcp`'s own references ever change.)

- [ ] **Step 2: Write the failing tests**

In `ReflexToolSurfaceTests.cs`, reuse `FakeToolRegistry`/`FakeToolExecutor` from `ReflexExecutorTests.cs` (same test project, `internal`) and a small local fake lookup:

```csharp
internal sealed class FakeLookup : IReflexRegistrationLookup
{
    public Dictionary<string, (string Skill, string File)> Tools { get; } = new();

    public bool TryGet(string toolName, out string skillName, out string filename)
    {
        if (Tools.TryGetValue(toolName, out var e)) { (skillName, filename) = e; return true; }
        skillName = ""; filename = ""; return false;
    }

    public IReadOnlyList<string> GetToolNames(string skillName) =>
        Tools.Where(kv => kv.Value.Skill == skillName).Select(kv => kv.Key).ToArray();
}
```

```csharp
[TestClass]
public class ReflexToolSurfaceTests
{
    private static (ReflexToolSurface Surface, FakeLookup Lookup, FakeToolRegistry Registry) NewSurface()
    {
        var lookup = new FakeLookup();
        var registry = new FakeToolRegistry();
        var surface = new ReflexToolSurface(registry, NullLogger<ReflexToolSurface>.Instance);
        surface.Bind(lookup);
        return (surface, lookup, registry);
    }

    private static void RegisterTool(FakeLookup lookup, FakeToolRegistry registry, string skill, string file, string name)
    {
        lookup.Tools[name] = (skill, file);
        registry.Register(new ToolRegistration { Name = name, Description = name, Source = "reflex" }, new FakeToolExecutor());
    }

    [TestMethod]
    public void ActivateByName_UnknownTool_ReturnsNull()
    {
        var (surface, _, _) = NewSurface();
        Assert.IsNull(surface.ActivateByName("s1", "reflex__x__y"));
    }

    [TestMethod]
    public void ActivateByName_KnownTool_ActivatesAndReturnsBoundFunction()
    {
        var (surface, lookup, registry) = NewSurface();
        RegisterTool(lookup, registry, "calendar/scan", "fanout.json", "reflex__calendar_scan__fanout_json");

        var fn = surface.ActivateByName("s1", "reflex__calendar_scan__fanout_json");

        Assert.IsNotNull(fn);
        Assert.AreEqual("reflex__calendar_scan__fanout_json", fn!.Name);
        Assert.AreEqual("s1", ((ISessionBoundTool)fn).SessionId);
        Assert.IsTrue(surface.IsActivated("s1", "reflex__calendar_scan__fanout_json"));
    }

    [TestMethod]
    public void GetActivated_EagerOrOffMode_ReturnsEmpty()
    {
        var (surface, lookup, registry) = NewSurface();
        RegisterTool(lookup, registry, "s", "f", "reflex__s__f");
        surface.ActivateByName("s1", "reflex__s__f");

        Assert.AreEqual(0, surface.GetActivated("s1", TypedToolMode.Eager).Count);
        Assert.AreEqual(0, surface.GetActivated("s1", TypedToolMode.Off).Count);
    }

    [TestMethod]
    public void GetActivated_Lazy_ReturnsActivatedToolsOldestFirst()
    {
        var (surface, lookup, registry) = NewSurface();
        RegisterTool(lookup, registry, "s", "a", "reflex__s__a");
        RegisterTool(lookup, registry, "s", "b", "reflex__s__b");
        surface.ActivateByName("s1", "reflex__s__a");
        surface.ActivateByName("s1", "reflex__s__b");

        var names = surface.GetActivated("s1", TypedToolMode.Lazy).Select(f => f.Name).ToArray();
        CollectionAssert.AreEqual(new[] { "reflex__s__a", "reflex__s__b" }, names);
    }

    [TestMethod]
    public void Activate_SameNameTwice_KeepsOriginalPosition()
    {
        var (surface, lookup, registry) = NewSurface();
        RegisterTool(lookup, registry, "s", "a", "reflex__s__a");
        RegisterTool(lookup, registry, "s", "b", "reflex__s__b");
        surface.ActivateByName("s1", "reflex__s__a");
        surface.ActivateByName("s1", "reflex__s__b");
        surface.ActivateByName("s1", "reflex__s__a"); // re-touch

        var names = surface.GetActivated("s1", TypedToolMode.Lazy).Select(f => f.Name).ToArray();
        CollectionAssert.AreEqual(new[] { "reflex__s__a", "reflex__s__b" }, names);
    }

    [TestMethod]
    public void Activate_OverCap_DropsOldest()
    {
        var (surface, lookup, registry) = NewSurface();
        for (var i = 0; i < 41; i++)
            RegisterTool(lookup, registry, "s", $"f{i}", $"reflex__s__f{i}");
        for (var i = 0; i < 41; i++)
            surface.ActivateByName("s1", $"reflex__s__f{i}");

        var names = surface.GetActivated("s1", TypedToolMode.Lazy).Select(f => f.Name).ToArray();
        Assert.AreEqual(40, names.Length);
        Assert.IsFalse(names.Contains("reflex__s__f0"), "oldest activation should have been dropped");
        Assert.IsTrue(names.Contains("reflex__s__f40"), "newest activation should remain");
    }

    [TestMethod]
    public void Pin_UnknownOrEmptySkill_NoOp()
    {
        var (surface, _, _) = NewSurface();
        surface.Pin("s1", "no-such-skill");
        Assert.AreEqual(0, surface.GetPinned("s1").Count);
    }

    [TestMethod]
    public void Pin_NullOrEmptySessionId_NoOp()
    {
        var (surface, lookup, registry) = NewSurface();
        RegisterTool(lookup, registry, "s", "a", "reflex__s__a");
        surface.Pin(null, "s");
        surface.Pin("", "s");
        Assert.AreEqual(0, surface.GetActivated("anything", TypedToolMode.Pinned).Count);
    }

    [TestMethod]
    public void Activate_NullOrEmptySessionId_NoOp()
    {
        var (surface, lookup, registry) = NewSurface();
        RegisterTool(lookup, registry, "s", "a", "reflex__s__a");
        Assert.IsNull(surface.ActivateByName(null!, "reflex__s__a"));
        Assert.IsNull(surface.ActivateByName("", "reflex__s__a"));
    }

    [TestMethod]
    public void Pin_KnownSkill_BringsAllItsReflexToolsIntoPinnedActivation()
    {
        var (surface, lookup, registry) = NewSurface();
        RegisterTool(lookup, registry, "calendar/scan", "fanout.json", "reflex__a");
        RegisterTool(lookup, registry, "calendar/scan", "poll.json", "reflex__b");

        surface.Pin("s1", "calendar/scan");

        var names = surface.GetActivated("s1", TypedToolMode.Pinned).Select(f => f.Name).ToArray();
        CollectionAssert.AreEquivalent(new[] { "reflex__a", "reflex__b" }, names);
    }

    [TestMethod]
    public void Pin_SameSkillTwice_RefreshesRecencyWithoutDuplicating()
    {
        var (surface, lookup, registry) = NewSurface();
        RegisterTool(lookup, registry, "s1", "a", "reflex__s1__a");
        RegisterTool(lookup, registry, "s2", "a", "reflex__s2__a");
        RegisterTool(lookup, registry, "s3", "a", "reflex__s3__a");
        surface.Pin("sess", "s1");
        surface.Pin("sess", "s2");
        surface.Pin("sess", "s1"); // re-touch s1

        CollectionAssert.AreEqual(new[] { "s2", "s1" }, surface.GetPinned("sess").ToArray());
    }

    [TestMethod]
    public void Pin_OverCap_DropsOldestPinnedSkill()
    {
        var (surface, lookup, registry) = NewSurface();
        foreach (var s in new[] { "s1", "s2", "s3", "s4" })
            RegisterTool(lookup, registry, s, "a", $"reflex__{s}__a");
        foreach (var s in new[] { "s1", "s2", "s3", "s4" })
            surface.Pin("sess", s);

        CollectionAssert.AreEqual(new[] { "s2", "s3", "s4" }, surface.GetPinned("sess").ToArray());
    }

    [TestMethod]
    public void GetActivated_Pinned_SkillLosesAllReflexTools_StopsAppearingForThatSkill()
    {
        var (surface, lookup, registry) = NewSurface();
        RegisterTool(lookup, registry, "s", "a", "reflex__s__a");
        surface.Pin("sess", "s");
        Assert.AreEqual(1, surface.GetActivated("sess", TypedToolMode.Pinned).Count, "precondition");

        lookup.Tools.Remove("reflex__s__a"); // reconciliation demoted/removed it

        Assert.AreEqual(0, surface.GetActivated("sess", TypedToolMode.Pinned).Count);
    }

    [TestMethod]
    public void Evict_RemovesFromEveryActivatedSession()
    {
        var (surface, lookup, registry) = NewSurface();
        RegisterTool(lookup, registry, "s", "a", "reflex__s__a");
        surface.ActivateByName("s1", "reflex__s__a");
        surface.ActivateByName("s2", "reflex__s__a");

        surface.Evict(new HashSet<string> { "reflex__s__a" });

        Assert.IsFalse(surface.IsActivated("s1", "reflex__s__a"));
        Assert.IsFalse(surface.IsActivated("s2", "reflex__s__a"));
    }

    [TestMethod]
    public void IsReflexTool_DelegatesToLookup()
    {
        var (surface, lookup, registry) = NewSurface();
        RegisterTool(lookup, registry, "s", "a", "reflex__s__a");
        Assert.IsTrue(surface.IsReflexTool("reflex__s__a"));
        Assert.IsFalse(surface.IsReflexTool("reflex__unknown"));
    }

    [TestMethod]
    public void UnboundSurface_EveryMemberNoOpsRatherThanThrows()
    {
        var surface = new ReflexToolSurface(new FakeToolRegistry(), NullLogger<ReflexToolSurface>.Instance);
        Assert.IsFalse(surface.IsReflexTool("anything"));
        Assert.IsNull(surface.ActivateByName("s1", "anything"));
        Assert.AreEqual(0, surface.GetActivated("s1", TypedToolMode.Pinned).Count);
        surface.Pin("s1", "anything"); // must not throw
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test RockBot.slnx --filter "FullyQualifiedName~ReflexToolSurfaceTests"`
Expected: build failure — `ReflexToolSurface` does not exist yet.

- [ ] **Step 4: Implement `ReflexToolSurface`**

Mirror `McpTypedToolSurface`'s `Session`/LRU/pin/idle-sweep structure (`src/RockBot.Tools.Mcp/McpTypedToolSurface.cs`) exactly, with these differences: no `IOptions<T>` (caps are the hardcoded constants from Global Constraints), `Pin`/`GetPinned` key on skill names instead of server names, `GetActivated`'s pinned-expansion loop calls `lookup.GetToolNames(skillName)` (live, not cached — this is what makes `GetActivated_Pinned_SkillLosesAllReflexTools_StopsAppearingForThatSkill` pass) instead of `catalog.WrappersFor(server)`, and building an `AIFunction` for a tool name goes through a private helper:

```csharp
private AIFunction? TryBuildFunction(string toolName, string sessionId)
{
    var registration = registry.GetTools().FirstOrDefault(t => t.Name == toolName);
    if (registration is null) return null;
    var executor = registry.GetExecutor(toolName);
    return executor is null ? null : new RegistryToolFunction(registration, executor, sessionId);
}
```

`Bind`/`_lookup` and the `Session` class (`Names: List<string>`, `Pinned: List<string>`, `Touched: DateTimeOffset`) are private/internal state, same shape as `McpTypedToolSurface`'s.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test RockBot.slnx --filter "FullyQualifiedName~ReflexToolSurfaceTests"`
Expected: all PASS.

- [ ] **Step 6: Run the full solution test suite**

Run: `dotnet test RockBot.slnx`
Expected: all projects pass, no regression.

- [ ] **Step 7: Commit**

```bash
git add src/RockBot.Tools.Reflex/ReflexToolSurface.cs tests/RockBot.Tools.Reflex.Tests/ReflexToolSurfaceTests.cs src/RockBot.Tools.Reflex/RockBot.Tools.Reflex.csproj
git commit -m "feat: add ReflexToolSurface (per-session LRU activation and skill pinning)"
```

---

### Task 3: `ReflexToolCatalog` evicts from the surface on demote/delete

**Files:**
- Modify: `src/RockBot.Tools.Reflex/ReflexToolCatalog.cs`
- Modify: `tests/RockBot.Tools.Reflex.Tests/ReflexToolCatalogTests.cs`
- Modify: `src/RockBot.Tools.Reflex/ReflexServiceCollectionExtensions.cs`

**Interfaces:**
- Consumes: `ReflexToolSurface.Evict(IReadOnlySet<string>)` (`internal`, Task 2 — reachable since both are in `RockBot.Tools.Reflex`, and the test project already has `InternalsVisibleTo`).
- Produces: `ReflexToolCatalog.Bind(ReflexToolSurface surface)` (`internal`), called once by DI wiring; no change to `ReflexToolCatalog`'s public constructor.

- [ ] **Step 1: Write the failing test**

In `ReflexToolCatalogTests.cs`:

```csharp
[TestMethod]
public async Task ReconcileOnceAsync_ResourceDemotedSinceLastCycle_EvictsFromBoundSurface()
{
    var resource = new SkillResource("fanout.json", SkillResourceType.Wisp, "d", Provisional: false);
    var skillStore = new FakeSkillStore
    {
        Skills = [new Skill("calendar/scan", "s", "c", DateTimeOffset.UtcNow, Manifest: [resource])],
    };
    var registry = new FakeToolRegistry();
    var catalog = NewCatalog(skillStore, registry);
    var surface = new ReflexToolSurface(registry, NullLogger<ReflexToolSurface>.Instance);
    surface.Bind((IReflexRegistrationLookup)catalog);
    catalog.Bind(surface);

    await catalog.ReconcileOnceAsync(CancellationToken.None);
    var name = ReflexToolNames.Build("calendar/scan", "fanout.json");
    surface.ActivateByName("sess", name);
    Assert.IsTrue(surface.IsActivated("sess", name), "precondition");

    skillStore.Skills[0] = skillStore.Skills[0] with { Manifest = [resource with { Provisional = true }] };
    await catalog.ReconcileOnceAsync(CancellationToken.None);

    Assert.IsFalse(surface.IsActivated("sess", name));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test RockBot.slnx --filter "FullyQualifiedName~ReconcileOnceAsync_ResourceDemotedSinceLastCycle_EvictsFromBoundSurface"`
Expected: build error (`ReflexToolCatalog` has no `Bind` method) or, once that compiles stubbed, a failure because eviction never happens.

- [ ] **Step 3: Add `Bind` and the `Evict` call**

In `ReflexToolCatalog.cs`, add a field and method alongside the existing `_byName`:

```csharp
private ReflexToolSurface? _surface;
internal void Bind(ReflexToolSurface surface) => _surface = surface;
```

In `ReconcileOnceAsync`, change the stale-unregister loop to collect names first, then evict after the lock releases (eviction takes its own lock per session internally, so it must not run while holding `_gate`):

```csharp
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
    // ... existing registration loop, unchanged ...
    _byName = registered;
}
if (stale.Count > 0)
    _surface?.Evict(stale.ToHashSet());
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test RockBot.slnx --filter "FullyQualifiedName~ReconcileOnceAsync_ResourceDemotedSinceLastCycle_EvictsFromBoundSurface"`
Expected: PASS.

- [ ] **Step 5: Wire the mutual bind in DI**

In `ReflexServiceCollectionExtensions.cs`, in `AddReflexTools`, register `ReflexToolSurface` as a singleton and extend the existing `ReflexToolCatalog` factory to bind both directions once both exist:

```csharp
builder.Services.AddSingleton<ReflexToolSurface>();
builder.Services.AddSingleton<ReflexToolCatalog>(sp =>
{
    var catalog = new ReflexToolCatalog(
        sp.GetRequiredService<ISkillStore>(),
        sp.GetRequiredService<IToolRegistry>(),
        () => sp.GetRequiredService<ReflexExecutor>(),
        sp.GetRequiredService<ILogger<ReflexToolCatalog>>());
    var surface = sp.GetRequiredService<ReflexToolSurface>();
    catalog.Bind(surface);
    surface.Bind(catalog);
    return catalog;
});
```

(Replace whatever the existing factory body is with this extended version — the constructor call itself is unchanged from Phase 2, only the two `Bind` calls are new.)

- [ ] **Step 6: Run the full solution test suite**

Run: `dotnet test RockBot.slnx`
Expected: all projects pass.

- [ ] **Step 7: Commit**

```bash
git add src/RockBot.Tools.Reflex/ReflexToolCatalog.cs tests/RockBot.Tools.Reflex.Tests/ReflexToolCatalogTests.cs src/RockBot.Tools.Reflex/ReflexServiceCollectionExtensions.cs
git commit -m "feat: reflex catalog evicts demoted/deleted tools from the session surface"
```

---

### Task 4: `IReflexActivationHooks` and `AgentContextBuilder` wiring (pin-on-touch, show-as-callable)

**Files:**
- Create: `src/RockBot.Host/IReflexActivationHooks.cs`
- Modify: `src/RockBot.Tools.Reflex/ReflexToolSurface.cs` (implement the new interface)
- Modify: `src/RockBot.Tools.Reflex/ReflexServiceCollectionExtensions.cs`
- Modify: `src/RockBot.Host/AgentContextBuilder.cs`
- Modify: `tests/RockBot.Host.Tests/AgentContextBuilderTests.cs` (exact file name: locate the existing test class covering the skill-index/skill-recall injection — search for `FormatResourceTag` or `shouldInjectSkillIndex` usage in `tests/RockBot.Host.Tests/` if the name differs from this guess)

**Interfaces:**
- Produces (`RockBot.Host`):

```csharp
public interface IReflexActivationHooks
{
    void OnSkillSurfaced(string? sessionId, string skillName);
    string? TryGetToolName(string skillName, string filename);
}
```

- Consumes (in `ReflexToolSurface`'s implementation): its own already-bound `_lookup` (Task 2) and `ReflexToolNames.Build(string, string)` (existing, Phase 2, `RockBot.Tools.Reflex`).

- [ ] **Step 1: Write the failing tests for `ReflexToolSurface : IReflexActivationHooks`**

Add to `ReflexToolSurfaceTests.cs`:

```csharp
[TestMethod]
public void OnSkillSurfaced_PinsTheSkill()
{
    var (surface, lookup, registry) = NewSurface();
    RegisterTool(lookup, registry, "s", "a", "reflex__s__a");

    ((IReflexActivationHooks)surface).OnSkillSurfaced("sess", "s");

    Assert.AreEqual(1, surface.GetActivated("sess", TypedToolMode.Pinned).Count);
}

[TestMethod]
public void TryGetToolName_ValidatedResourceWithRegisteredReflex_ReturnsItsName()
{
    var (surface, lookup, registry) = NewSurface();
    var name = ReflexToolNames.Build("calendar/scan", "fanout.json");
    RegisterTool(lookup, registry, "calendar/scan", "fanout.json", name);

    Assert.AreEqual(name, ((IReflexActivationHooks)surface).TryGetToolName("calendar/scan", "fanout.json"));
}

[TestMethod]
public void TryGetToolName_NotCurrentlyRegistered_ReturnsNull()
{
    var (surface, _, _) = NewSurface();
    Assert.IsNull(((IReflexActivationHooks)surface).TryGetToolName("calendar/scan", "fanout.json"));
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test RockBot.slnx --filter "FullyQualifiedName~ReflexToolSurfaceTests"`
Expected: build error — `ReflexToolSurface` doesn't implement `IReflexActivationHooks` (which doesn't exist yet either).

- [ ] **Step 3: Create `IReflexActivationHooks` and implement it on `ReflexToolSurface`**

Create `src/RockBot.Host/IReflexActivationHooks.cs` with the interface shown above (XML doc: "Optional hook from skill-recall context injection into reflex tool activation (#reflex-tools Phase 3). Null when reflex tools aren't installed.").

In `ReflexToolSurface.cs`, add `, IReflexActivationHooks` to the class declaration and implement explicitly:

```csharp
void IReflexActivationHooks.OnSkillSurfaced(string? sessionId, string skillName) => Pin(sessionId, skillName);

string? IReflexActivationHooks.TryGetToolName(string skillName, string filename)
{
    if (_lookup is not { } lookup) return null;
    var candidate = ReflexToolNames.Build(skillName, filename);
    return lookup.TryGet(candidate, out var actualSkill, out var actualFile)
        && actualSkill == skillName && actualFile == filename
        ? candidate
        : null;
}
```

- [ ] **Step 4: Run to verify the new tests pass**

Run: `dotnet test RockBot.slnx --filter "FullyQualifiedName~ReflexToolSurfaceTests"`
Expected: all PASS.

- [ ] **Step 5: Register `IReflexActivationHooks` in DI**

In `ReflexServiceCollectionExtensions.cs`'s `AddReflexTools`, add:

```csharp
builder.Services.AddSingleton<IReflexActivationHooks>(sp => sp.GetRequiredService<ReflexToolSurface>());
```

- [ ] **Step 6: Write the failing `AgentContextBuilder` tests**

Locate the existing test fixture that builds `AgentContextBuilder` and exercises the skill index / skill-recall path (search `tests/RockBot.Host.Tests/` for `FormatResourceTag` or `EnableSkillInjection` to find the exact file and its existing fake-construction helper). Add, using that fixture's existing conventions:

1. `SkillIndex_NoReflexHooks_RendersUnchanged` — build the context with `reflexHooks: null` (or whatever default the fixture already uses, confirming it's null), assert the rendered skill-index text for a skill with a validated Wisp resource is byte-identical to today's `FormatResourceTag`-only output (no "(direct: ...)" suffix, no exception).
2. `SkillIndex_ValidatedWispWithRegisteredReflex_ShowsDirectlyCallableToolName` — supply a fake `IReflexActivationHooks` whose `TryGetToolName(skillName, filename)` returns a fixed tool name for one resource; assert the rendered skill-index line for that skill includes that tool name (exact surrounding text is the implementer's call — e.g. `" (direct: {name})"` appended after `FormatResourceTag`'s own tag — the test should assert the tool name appears in that skill's line, not pin exact punctuation).
3. `SkillRecall_NewlyRecalledSkill_CallsOnSkillSurfaced` — supply a fake `IReflexActivationHooks` recording calls; run a turn whose BM25 recall (`skillSearchTask`/`recalledSkills`) surfaces a skill not previously recalled in the session; assert `OnSkillSurfaced(sessionId, thatSkill.Name)` was called exactly once. A skill recalled again on a later turn in the same session (blocked by `skillRecallTracker.TryMarkAsRecalled`) must not call it again.

- [ ] **Step 7: Run to verify they fail**

Run: `dotnet test RockBot.slnx --filter "FullyQualifiedName~SkillIndex_NoReflexHooks_RendersUnchanged|SkillIndex_ValidatedWispWithRegisteredReflex_ShowsDirectlyCallableToolName|SkillRecall_NewlyRecalledSkill_CallsOnSkillSurfaced"`
Expected: build error (no `reflexHooks` parameter yet) or the display/pin behavior simply absent.

- [ ] **Step 8: Wire `AgentContextBuilder`**

Add `IReflexActivationHooks? reflexHooks` as the last parameter of `AgentContextBuilder`'s existing primary constructor (`AgentContextBuilder.cs:20`-ish).

In the skill-index block (~line 615-632), add a private helper and use it:

```csharp
private string FormatReflexSuffix(Skill skill)
{
    if (reflexHooks is null) return "";
    var names = (skill.Manifest ?? [])
        .Where(r => r.Type == SkillResourceType.Wisp && !r.Provisional)
        .Select(r => reflexHooks.TryGetToolName(skill.Name, r.Filename))
        .Where(n => n is not null)
        .ToList();
    return names.Count > 0 ? $" (direct: {string.Join(", ", names)})" : "";
}
```

```csharp
var resourceTag = SkillTools.FormatResourceTag(s.Manifest);
return $"- {s.Name}{resourceTag}{FormatReflexSuffix(s)}: {summary}";
```

In the per-turn recall block, right after `newSkills` is computed (~line 640-642):

```csharp
foreach (var s in newSkills)
    reflexHooks?.OnSkillSurfaced(sessionId, s.Name);
```

Apply the same two edits to the worker variant (`AgentContextBuilder.cs`, the block around lines 1207-1269 that mirrors the primary one — same `FormatReflexSuffix` helper, same `newSkills` loop placement).

- [ ] **Step 9: Run to verify they pass**

Run: `dotnet test RockBot.slnx --filter "FullyQualifiedName~SkillIndex_NoReflexHooks_RendersUnchanged|SkillIndex_ValidatedWispWithRegisteredReflex_ShowsDirectlyCallableToolName|SkillRecall_NewlyRecalledSkill_CallsOnSkillSurfaced"`
Expected: all PASS.

- [ ] **Step 10: Run the full solution test suite**

Run: `dotnet test RockBot.slnx`
Expected: all projects pass, no regression in any existing `AgentContextBuilder`/skill-index test.

- [ ] **Step 11: Commit**

```bash
git add src/RockBot.Host/IReflexActivationHooks.cs src/RockBot.Tools.Reflex/ReflexToolSurface.cs src/RockBot.Tools.Reflex/ReflexServiceCollectionExtensions.cs src/RockBot.Host/AgentContextBuilder.cs tests/RockBot.Host.Tests/
git commit -m "feat: skill recall pins a touched skill's reflexes and shows them as directly callable"
```

---

### Task 5: `CompositeTypedToolSurface` and Program.cs wiring

**Files:**
- Create: `src/RockBot.Tools.Reflex/CompositeTypedToolSurface.cs`
- Create: `tests/RockBot.Tools.Reflex.Tests/CompositeTypedToolSurfaceTests.cs`
- Modify: `src/RockBot.Agent/Program.cs`

**Interfaces:**
- Consumes: `McpTypedToolSurface` (`public sealed class`, `RockBot.Tools.Mcp`, public constructor `McpTypedToolSurface(IOptions<McpToolSurfaceOptions> options, ILogger<McpTypedToolSurface> logger, TimeProvider? time = null)`); `ReflexToolSurface` (Task 2/4).
- Produces: `public sealed class CompositeTypedToolSurface(McpTypedToolSurface mcp, ReflexToolSurface reflex) : ITypedToolSurface`, consumed only by Program.cs's DI wiring (no other code references it directly).

- [ ] **Step 1: Write the failing tests**

```csharp
[TestClass]
public class CompositeTypedToolSurfaceTests
{
    private static (CompositeTypedToolSurface Composite, ReflexToolSurface Reflex, FakeLookup Lookup, FakeToolRegistry Registry) New()
    {
        var mcp = new McpTypedToolSurface(Options.Create(new McpToolSurfaceOptions()), NullLogger<McpTypedToolSurface>.Instance);
        var registry = new FakeToolRegistry();
        var lookup = new FakeLookup();
        var reflex = new ReflexToolSurface(registry, NullLogger<ReflexToolSurface>.Instance);
        reflex.Bind(lookup);
        return (new CompositeTypedToolSurface(mcp, reflex), reflex, lookup, registry);
    }

    [TestMethod]
    public void ModeFor_DelegatesToMcp()
    {
        var (composite, _, _, _) = New();
        // McpToolSurfaceOptions defaults to Off for every tier with no config bound.
        Assert.AreEqual(TypedToolMode.Off, composite.ModeFor(ModelTier.Low));
    }

    [TestMethod]
    public void IsTypedTool_TrueWhenEitherSurfaceClaimsIt()
    {
        var (composite, _, lookup, registry) = New();
        lookup.Tools["reflex__s__a"] = ("s", "a");
        registry.Register(new ToolRegistration { Name = "reflex__s__a", Description = "d", Source = "reflex" }, new FakeToolExecutor());

        Assert.IsTrue(composite.IsTypedTool("reflex__s__a"));
        Assert.IsFalse(composite.IsTypedTool("unknown_tool"));
    }

    [TestMethod]
    public void ActivateByName_UnknownToEither_ReturnsNull()
    {
        var (composite, _, _, _) = New();
        Assert.IsNull(composite.ActivateByName("s1", "unknown_tool"));
    }

    [TestMethod]
    public void ActivateByName_KnownReflexTool_ActivatesViaReflexSurface()
    {
        var (composite, reflex, lookup, registry) = New();
        lookup.Tools["reflex__s__a"] = ("s", "a");
        registry.Register(new ToolRegistration { Name = "reflex__s__a", Description = "d", Source = "reflex" }, new FakeToolExecutor());

        var fn = composite.ActivateByName("s1", "reflex__s__a");

        Assert.IsNotNull(fn);
        Assert.IsTrue(reflex.IsActivated("s1", "reflex__s__a"));
    }

    [TestMethod]
    public void GetActivated_ConcatenatesMcpThenReflex()
    {
        var (composite, reflex, lookup, registry) = New();
        lookup.Tools["reflex__s__a"] = ("s", "a");
        registry.Register(new ToolRegistration { Name = "reflex__s__a", Description = "d", Source = "reflex" }, new FakeToolExecutor());
        reflex.ActivateByName("s1", "reflex__s__a");

        var names = composite.GetActivated("s1", TypedToolMode.Lazy).Select(f => f.Name).ToArray();
        CollectionAssert.AreEqual(new[] { "reflex__s__a" }, names); // Mcp's surface has nothing activated for s1
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test RockBot.slnx --filter "FullyQualifiedName~CompositeTypedToolSurfaceTests"`
Expected: build error — `CompositeTypedToolSurface` doesn't exist.

- [ ] **Step 3: Implement `CompositeTypedToolSurface`**

```csharp
public sealed class CompositeTypedToolSurface(McpTypedToolSurface mcp, ReflexToolSurface reflex) : ITypedToolSurface
{
    public TypedToolMode ModeFor(ModelTier tier) => mcp.ModeFor(tier);
    public string LoaderToolName => mcp.LoaderToolName;
    public string? Orientation(TypedToolMode mode) => mcp.Orientation(mode);
    public int MaxToolsPerRequest => mcp.MaxToolsPerRequest;

    public bool IsTypedTool(string toolName) => mcp.IsTypedTool(toolName) || reflex.IsReflexTool(toolName);

    public IReadOnlyList<AIFunction> GetActivated(string toolSessionId, TypedToolMode mode) =>
        [.. mcp.GetActivated(toolSessionId, mode), .. reflex.GetActivated(toolSessionId, mode)];

    public AIFunction? ActivateByName(string toolSessionId, string toolName) =>
        mcp.ActivateByName(toolSessionId, toolName) ?? reflex.ActivateByName(toolSessionId, toolName);
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test RockBot.slnx --filter "FullyQualifiedName~CompositeTypedToolSurfaceTests"`
Expected: all PASS.

- [ ] **Step 5: Wire Program.cs**

In `src/RockBot.Agent/Program.cs`, after the existing `agent.AddReflexTools(...)` call (line ~399, itself after `agent.AddMcpToolProxy(...)` at line ~385), add:

```csharp
agent.Services.AddSingleton<ITypedToolSurface>(sp => new CompositeTypedToolSurface(
    sp.GetRequiredService<McpTypedToolSurface>(),
    sp.GetRequiredService<ReflexToolSurface>()));
```

(This is a second `AddSingleton<ITypedToolSurface>` registration; the container resolves the last one registered, so this supersedes `AddMcpToolProxy`'s own `AddSingleton<ITypedToolSurface>(sp => sp.GetRequiredService<McpTypedToolSurface>())` without editing `McpServiceCollectionExtensions.cs`.)

- [ ] **Step 6: Build the whole solution**

Run: `dotnet build RockBot.slnx`
Expected: builds clean (`Program.cs` has no dedicated unit test; this is the verification, matching how Phase 2 verified its own `Program.cs` edit).

- [ ] **Step 7: Run the full solution test suite**

Run: `dotnet test RockBot.slnx`
Expected: all projects pass.

- [ ] **Step 8: Commit**

```bash
git add src/RockBot.Tools.Reflex/CompositeTypedToolSurface.cs tests/RockBot.Tools.Reflex.Tests/CompositeTypedToolSurfaceTests.cs src/RockBot.Agent/Program.cs
git commit -m "feat: reflex tools join MCP's typed tool surface via a composite, mirroring its mode"
```

---

### Task 6: `TryGetLoader` accepts any session-bound tool, not only the named loader

**Files:**
- Modify: `src/RockBot.Host/TypedToolSurface.cs`
- Create: `tests/RockBot.Host.Tests/TypedToolSurfaceContextTests.cs`

**Interfaces:**
- No new public surface; behavioral fix inside `TypedToolSurfaceContext.TryGetLoader` (private, called by `AddActivated`/`TryActivate`).

- [ ] **Step 1: Write the failing test**

Create `TypedToolSurfaceContextTests.cs` with a minimal fake surface (no MCP/Reflex dependency needed — this is a host-level concern):

```csharp
using Microsoft.Extensions.AI;
using RockBot.Host;

namespace RockBot.Host.Tests;

[TestClass]
public class TypedToolSurfaceContextTests
{
    private sealed class FakeSurface : ITypedToolSurface
    {
        public Dictionary<string, AIFunction> Activated { get; } = new();
        public TypedToolMode ModeFor(ModelTier tier) => TypedToolMode.Lazy;
        public string LoaderToolName => "mcp_find_tools";
        public bool IsTypedTool(string toolName) => toolName.StartsWith("typed_", StringComparison.Ordinal);
        public IReadOnlyList<AIFunction> GetActivated(string toolSessionId, TypedToolMode mode) => [.. Activated.Values];
        public AIFunction? ActivateByName(string toolSessionId, string toolName) => null;
    }

    private sealed class FakeSessionBoundFunction(string name, string? sessionId) : AIFunction, ISessionBoundTool
    {
        public string? SessionId => sessionId;
        public override string Name => name;
        public override System.Text.Json.JsonElement JsonSchema { get; } =
            System.Text.Json.JsonDocument.Parse("{}").RootElement;
        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            ValueTask.FromResult<object?>(null);
    }

    [TestMethod]
    public void AddActivated_NoLoaderToolPresent_StillActivatesFromAnySessionBoundTool()
    {
        var surface = new FakeSurface();
        surface.Activated["typed_x"] = new FakeSessionBoundFunction("typed_x", null);
        using var scope = TypedToolSurfaceContext.Set(surface, ModelTier.Low);

        // No tool named "mcp_find_tools" in the list at all — only some other session-bound tool.
        var options = new ChatOptions { Tools = [new FakeSessionBoundFunction("some_other_tool", "sess-1")] };

        var added = TypedToolSurfaceContext.AddActivated(options);

        Assert.AreEqual(1, added);
        Assert.IsTrue(options.Tools!.Any(t => t.Name == "typed_x"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test RockBot.slnx --filter "FullyQualifiedName~AddActivated_NoLoaderToolPresent_StillActivatesFromAnySessionBoundTool"`
Expected: FAIL — `added == 0` (today's `TryGetLoader` only matches a tool named exactly `LoaderToolName`).

- [ ] **Step 3: Relax `TryGetLoader`**

In `src/RockBot.Host/TypedToolSurface.cs`, change:

```csharp
var found = tools.OfType<AIFunction>().FirstOrDefault(t => t.Name == current.LoaderToolName);
```

to:

```csharp
var found = tools.OfType<AIFunction>()
    .FirstOrDefault(t => t.GetService<ISessionBoundTool>()?.SessionId is { Length: > 0 });
```

(The surrounding null-check on `found?.GetService<ISessionBoundTool>()?.SessionId` becomes redundant with this change but can stay as defensive belt-and-suspenders, or be simplified — implementer's call, either compiles and passes.)

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test RockBot.slnx --filter "FullyQualifiedName~AddActivated_NoLoaderToolPresent_StillActivatesFromAnySessionBoundTool"`
Expected: PASS.

- [ ] **Step 5: Run the full solution test suite**

Run: `dotnet test RockBot.slnx`
Expected: all projects pass — in particular every existing MCP Lazy/Pinned end-to-end test (`McpLazyWrapperEndToEndTests`, `McpHandbackAgentSideTests`, `McpLazyTypedToolsTests`), none of which stop including `mcp_find_tools` in their own `options.Tools`, so this relaxation changes nothing observable for them.

- [ ] **Step 6: Commit**

```bash
git add src/RockBot.Host/TypedToolSurface.cs tests/RockBot.Host.Tests/TypedToolSurfaceContextTests.cs
git commit -m "fix: typed-tool activation anchors on any session-bound tool, not only the named loader"
```
