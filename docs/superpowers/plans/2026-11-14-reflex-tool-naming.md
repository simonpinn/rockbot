# Reflex Tool Naming Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a reflex tool's registered name stable forever (never changes when its owning skill is renamed) and always well under the provider's 64-char tool-name limit, by anchoring it to the resource's `DefinitionHash` instead of to the skill's current display name.

**Architecture:** `ReflexToolNames.Build` drops its `(skillName, filename)` inputs in favor of a single `definitionHash` input, producing `reflex__{definitionHash}` (24 chars, fixed). Its two call sites — `ReflexToolCatalog.ReconcileOnceAsync` (registration) and `IReflexActivationHooks.TryGetToolName` (lookup hint for context injection) — are updated to pass the resource's `DefinitionHash` and to skip gracefully when it's null.

**Tech Stack:** .NET 10, MSTest, Rocks-style hand-written fakes already present in these test files (no mocking framework needed for this change).

**Spec:** `docs/superpowers/specs/2026-11-14-reflex-tool-naming-design.md`

## Global Constraints

- Tool names must satisfy `^[a-zA-Z0-9_-]{1,64}$` (checked via `McpWrapperNaming.FitsProviderLimit`).
- `DefinitionHash` is SHA-256-hex16 — 16 lowercase hex characters — per `SkillResource.DefinitionHash`'s doc comment, but is `string?` (nullable): null for resources saved before the field existed or of a non-hashed type.
- No behavior change to how a reflex tool is selected, fired, or reported — only how its name is constructed.
- Zero migration risk: no live stimulus patterns or registered reflex tools reference the old naming scheme today (confirmed: the host's skills directory currently has no non-null manifest except the one resource this fix unblocks).

## Review Focus

- A resource with `DefinitionHash == null` (legacy/non-hashed) must be skipped with a warning, never thrown on.
- Two resources (different skill and/or filename) that happen to share the same `DefinitionHash` must still result in exactly one registered tool, with the second skipped and logged — not a crash, not two registrations.
- `TryGetToolName` called with `definitionHash: null` must return `null`, never build or match a bogus `"reflex__"` name.
- A skill renamed between two reconciliation cycles, with its resource's `DefinitionHash` unchanged, must keep the exact same registered tool name across the rename (the core property this plan exists to deliver).
- A `DefinitionHash` value containing a character outside `^[a-zA-Z0-9_-]+$` (defensive — shouldn't occur in practice since it's always hex) must still sanitize into a provider-safe name rather than violate the charset.

---

### Task 1: `ReflexToolNames.Build` keyed on `DefinitionHash`

**Files:**
- Modify: `src/RockBot.Tools.Reflex/ReflexToolNames.cs`
- Test: `tests/RockBot.Tools.Reflex.Tests/ReflexToolNamesTests.cs`

**Interfaces:**
- Produces: `ReflexToolNames.Build(string definitionHash) -> string`, replacing the existing `Build(string skillName, string filename) -> string`. Still returns `reflex__{sanitized}` (`Prefix`, `Separator` constants unchanged).

- [ ] **Step 1: Replace the test file's contents**

```csharp
namespace RockBot.Tools.Reflex.Tests;

[TestClass]
public class ReflexToolNamesTests
{
    [TestMethod]
    public void Build_DefinitionHash_PrependsPrefixAndSeparator()
    {
        Assert.AreEqual("reflex__da8b3366e7837c2c", ReflexToolNames.Build("da8b3366e7837c2c"));
    }

    [TestMethod]
    public void Build_HashWithDisallowedCharacters_SanitizesToHyphen()
    {
        // Defensive: DefinitionHash is always hex in practice, but Build must never emit a
        // character outside ^[a-zA-Z0-9_-]{1,64}$ regardless of its input.
        Assert.AreEqual("reflex__ab-cd", ReflexToolNames.Build("ab.cd"));
    }
}
```

This replaces the whole existing file (the old tests exercised skill-name/filename sanitization and the "never truncates a 100-char skill name" case, both meaningless once `Build` no longer takes a skill name or filename).

- [ ] **Step 2: Run the test file to verify it fails to compile/fails**

Run: `dotnet test tests/RockBot.Tools.Reflex.Tests --filter "FullyQualifiedName~ReflexToolNamesTests"`
Expected: compile error (`Build` still takes two arguments) — confirms the test is exercising the not-yet-changed code.

- [ ] **Step 3: Implement the new `Build` in `src/RockBot.Tools.Reflex/ReflexToolNames.cs`**

```csharp
public static class ReflexToolNames
{
    public const string Prefix = "reflex";
    public const string Separator = "__";

    public static string Build(string definitionHash) =>
        $"{Prefix}{Separator}{Sanitize(definitionHash)}";

    private static string Sanitize(string value) =>
        value.All(IsAllowed) ? value : new string(value.Select(c => IsAllowed(c) ? c : '-').ToArray());

    private static bool IsAllowed(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '-';
}
```

Update the class's XML doc comment to describe the new contract: built from the resource's stable `DefinitionHash` rather than its (mutable) skill name and filename, so the name never changes across a skill rename — replacing the old comment's description of the two-part scheme.

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/RockBot.Tools.Reflex.Tests --filter "FullyQualifiedName~ReflexToolNamesTests"`
Expected: PASS (2/2)

- [ ] **Step 5: Commit**

```bash
git add src/RockBot.Tools.Reflex/ReflexToolNames.cs tests/RockBot.Tools.Reflex.Tests/ReflexToolNamesTests.cs
git commit -m "refactor: build reflex tool names from DefinitionHash, not skill name"
```

---

### Task 2: `ReflexToolCatalog` registers by `DefinitionHash`

**Files:**
- Modify: `src/RockBot.Tools.Reflex/ReflexToolCatalog.cs:38-64` (the per-resource loop inside `ReconcileOnceAsync`)
- Test: `tests/RockBot.Tools.Reflex.Tests/ReflexToolCatalogTests.cs`

**Interfaces:**
- Consumes: `ReflexToolNames.Build(string definitionHash)` from Task 1.
- Produces: no change to `ReflexToolCatalog`'s public surface (`ReconcileOnceAsync`, `IReflexRegistrationLookup.TryGet`/`GetToolNames`, `Bind`) — only how the registered name is derived.

- [ ] **Step 1: Update the existing tests' `SkillResource` constructions and `ReflexToolNames.Build` call sites**

Every `SkillResource(...)` in this file that is meant to register (non-provisional, type `Wisp`) needs an explicit `DefinitionHash:` argument, and every `ReflexToolNames.Build("calendar/scan", "fanout.json")`-style call changes to `ReflexToolNames.Build("<that resource's hash>")`. Apply this one-for-one across the file:

| Test | Resource(s) gain | `Build(...)` call becomes |
|---|---|---|
| `ReconcileOnceAsync_ValidatedWispResource_RegistersReflexTool` | `DefinitionHash: "hash1"` on `fanout.json` | `ReflexToolNames.Build("hash1")` |
| `ReconcileOnceAsync_ResourceDemotedSinceLastCycle_UnregistersTool` | `DefinitionHash: "hash1"` on the shared `resource` | `ReflexToolNames.Build("hash1")` |
| `ReconcileOnceAsync_RegistryThrowsOnRegister_NameNotTrackedAsOwned_RetriedNextCycle` | `DefinitionHash: "hash1"` on `fanout.json` | `ReflexToolNames.Build("hash1")` |
| `ReconcileOnceAsync_ResourceDemotedSinceLastCycle_EvictsFromBoundSurface` | `DefinitionHash: "hash1"` on the shared `resource` | `ReflexToolNames.Build("hash1")` |
| `GetToolNames_ReturnsOnlyToolsRegisteredForThatSkill` | `DefinitionHash: "hashA"` on `calendar/scan`'s `fanout.json`; `DefinitionHash: "hashB"` on `other/skill`'s `x.json` | `ReflexToolNames.Build("hashA")` |

`ReconcileOnceAsync_ProvisionalOrNonWispResource_NeverRegistered` is unchanged (neither resource ever reaches the hash check — one is provisional, one isn't a Wisp).

- [ ] **Step 2: Replace `ReconcileOnceAsync_TwoSkillsSanitizeToSameToolName_SkipsSecondWithoutThrowing`**

Its premise (two skill *names* sanitizing to the same string) no longer exists once the name drops skill name entirely. Replace it with the real collision this scheme can still produce — two distinct resources sharing one `DefinitionHash`:

```csharp
[TestMethod]
public async Task ReconcileOnceAsync_TwoResourcesShareDefinitionHash_SkipsSecondWithoutThrowing()
{
    var skillStore = new FakeSkillStore
    {
        Skills =
        [
            new Skill("skill-a", "s", "c", DateTimeOffset.UtcNow, Manifest:
                [new SkillResource("x.json", SkillResourceType.Wisp, "d", Provisional: false, DefinitionHash: "samehash")]),
            new Skill("skill-b", "s", "c", DateTimeOffset.UtcNow, Manifest:
                [new SkillResource("y.json", SkillResourceType.Wisp, "d", Provisional: false, DefinitionHash: "samehash")]),
        ],
    };
    var registry = new FakeToolRegistry();
    var catalog = NewCatalog(skillStore, registry);

    await catalog.ReconcileOnceAsync(CancellationToken.None);

    var name = ReflexToolNames.Build("samehash");
    Assert.AreEqual(1, registry.GetTools().Count(t => t.Name == name));
}
```

- [ ] **Step 3: Replace `ReconcileOnceAsync_NameLongerThanProviderLimit_SkipsRegistration`**

Its premise (a 100-char skill name overflowing 64 chars) is unreachable once skill name no longer feeds the built name. Replace it with the new skip condition:

```csharp
[TestMethod]
public async Task ReconcileOnceAsync_ResourceWithNoDefinitionHash_SkipsRegistrationWithoutThrowing()
{
    var skillStore = new FakeSkillStore
    {
        Skills =
        [
            new Skill("calendar/scan", "s", "c", DateTimeOffset.UtcNow, Manifest:
                [new SkillResource("x.json", SkillResourceType.Wisp, "d", Provisional: false, DefinitionHash: null)]),
        ],
    };
    var registry = new FakeToolRegistry();
    var catalog = NewCatalog(skillStore, registry);

    await catalog.ReconcileOnceAsync(CancellationToken.None);

    Assert.AreEqual(0, registry.GetTools().Count);
}
```

- [ ] **Step 4: Add the rename-stability test proving this plan's core property**

```csharp
[TestMethod]
public async Task ReconcileOnceAsync_SkillRenamedBetweenCycles_ToolNameRemainsRegistered()
{
    var resource = new SkillResource("fanout.json", SkillResourceType.Wisp, "d", Provisional: false, DefinitionHash: "stablehash1");
    var skillStore = new FakeSkillStore
    {
        Skills = [new Skill("calendar/scan", "s", "c", DateTimeOffset.UtcNow, Manifest: [resource])],
    };
    var registry = new FakeToolRegistry();
    var catalog = NewCatalog(skillStore, registry);
    await catalog.ReconcileOnceAsync(CancellationToken.None);
    var name = ReflexToolNames.Build("stablehash1");
    Assert.IsTrue(registry.GetTools().Any(t => t.Name == name), "precondition: registered after cycle 1");

    // A dream rename changed the skill's display name; the resource (and its hash) are unchanged.
    skillStore.Skills[0] = skillStore.Skills[0] with { Name = "calendar/scan-renamed" };
    await catalog.ReconcileOnceAsync(CancellationToken.None);

    Assert.IsTrue(registry.GetTools().Any(t => t.Name == name), "same hash must keep the same tool name across a rename");
    Assert.IsTrue(((IReflexRegistrationLookup)catalog).TryGet(name, out var skillName, out _));
    Assert.AreEqual("calendar/scan-renamed", skillName, "lookup must reflect the resource's current skill name");
}
```

- [ ] **Step 5: Run the full test class to verify everything still fails to compile against old code**

Run: `dotnet test tests/RockBot.Tools.Reflex.Tests --filter "FullyQualifiedName~ReflexToolCatalogTests"`
Expected: compile error (`ReflexToolNames.Build` still takes two arguments in `ReflexToolCatalog.cs`).

- [ ] **Step 6: Update the per-resource loop in `ReconcileOnceAsync` (`src/RockBot.Tools.Reflex/ReflexToolCatalog.cs:38-64`)**

```csharp
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
```

Only the `(skill.Name, filename)` → `(definitionHash)` substitution, the new null-hash guard, and the collision log's wording (now a duplicate hash, not a sanitized-name collision) are new; the rest of `ReconcileOnceAsync` is untouched. Keep the `FitsProviderLimit` check as-is — it's now trivially satisfied by construction, but stays as the defensive backstop it already was for `McpWrapperCatalog`.

- [ ] **Step 7: Run the full test class to verify it passes**

Run: `dotnet test tests/RockBot.Tools.Reflex.Tests --filter "FullyQualifiedName~ReflexToolCatalogTests"`
Expected: PASS (all tests)

- [ ] **Step 8: Commit**

```bash
git add src/RockBot.Tools.Reflex/ReflexToolCatalog.cs tests/RockBot.Tools.Reflex.Tests/ReflexToolCatalogTests.cs
git commit -m "fix: register reflex tools by DefinitionHash so renames can't break or overflow their name"
```

---

### Task 3: `IReflexActivationHooks.TryGetToolName` takes the resource's `DefinitionHash`

**Files:**
- Modify: `src/RockBot.Host/IReflexActivationHooks.cs`
- Modify: `src/RockBot.Tools.Reflex/ReflexToolSurface.cs:240-249`
- Modify: `src/RockBot.Host/AgentContextBuilder.cs:73`
- Modify: `tests/RockBot.Host.Tests/AgentContextBuilderSkillRecallFramingTests.cs:329-330` (`FakeReflexActivationHooks`)
- Test: `tests/RockBot.Tools.Reflex.Tests/ReflexToolSurfaceTests.cs`

**Interfaces:**
- Consumes: `ReflexToolNames.Build(string definitionHash)` from Task 1.
- Produces: `IReflexActivationHooks.TryGetToolName(string skillName, string filename, string? definitionHash) -> string?` — the `definitionHash` parameter is new; callers pass the resource's own `DefinitionHash` (nullable, matching `SkillResource.DefinitionHash`'s type).

- [ ] **Step 1: Update the two `TryGetToolName` tests in `ReflexToolSurfaceTests.cs`**

```csharp
[TestMethod]
public void TryGetToolName_ValidatedResourceWithRegisteredReflex_ReturnsItsName()
{
    var (surface, lookup, registry) = NewSurface();
    var hash = "da8b3366e7837c2c";
    var name = ReflexToolNames.Build(hash);
    RegisterTool(lookup, registry, "calendar/scan", "fanout.json", name);

    Assert.AreEqual(name, ((IReflexActivationHooks)surface).TryGetToolName("calendar/scan", "fanout.json", hash));
}

[TestMethod]
public void TryGetToolName_NotCurrentlyRegistered_ReturnsNull()
{
    var (surface, _, _) = NewSurface();
    Assert.IsNull(((IReflexActivationHooks)surface).TryGetToolName("calendar/scan", "fanout.json", "da8b3366e7837c2c"));
}

[TestMethod]
public void TryGetToolName_NullDefinitionHash_ReturnsNull()
{
    var (surface, lookup, registry) = NewSurface();
    RegisterTool(lookup, registry, "calendar/scan", "fanout.json", "reflex__anything");

    Assert.IsNull(((IReflexActivationHooks)surface).TryGetToolName("calendar/scan", "fanout.json", null));
}
```

- [ ] **Step 2: Run the test class to verify it fails to compile**

Run: `dotnet test tests/RockBot.Tools.Reflex.Tests --filter "FullyQualifiedName~ReflexToolSurfaceTests"`
Expected: compile error (`TryGetToolName` still takes two arguments).

- [ ] **Step 3: Update `IReflexActivationHooks.TryGetToolName`'s signature in `src/RockBot.Host/IReflexActivationHooks.cs`**

```csharp
/// <summary>
/// The currently-registered reflex tool name for the resource at
/// <paramref name="skillName"/>/<paramref name="filename"/> with the given
/// <paramref name="definitionHash"/>, or null if none is registered or
/// <paramref name="definitionHash"/> is null.
/// </summary>
string? TryGetToolName(string skillName, string filename, string? definitionHash);
```

- [ ] **Step 4: Update `ReflexToolSurface`'s implementation (`src/RockBot.Tools.Reflex/ReflexToolSurface.cs:240-249`)**

```csharp
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
```

- [ ] **Step 5: Update the call site in `src/RockBot.Host/AgentContextBuilder.cs:73`**

```csharp
.Select(r => reflexHooks.TryGetToolName(skill.Name, r.Filename, r.DefinitionHash))
```

- [ ] **Step 6: Update `FakeReflexActivationHooks` in `tests/RockBot.Host.Tests/AgentContextBuilderSkillRecallFramingTests.cs:329-330`**

```csharp
public string? TryGetToolName(string skillName, string filename, string? definitionHash) =>
    ToolNames.TryGetValue((skillName, filename), out var name) ? name : null;
```

(The fake's own stub dictionary is already keyed by `(skillName, filename)` and doesn't need to validate the hash — this is a signature-only change so the fake keeps compiling and behaving the same.)

- [ ] **Step 7: Run both test classes to verify they pass**

Run: `dotnet test tests/RockBot.Tools.Reflex.Tests --filter "FullyQualifiedName~ReflexToolSurfaceTests"`
Expected: PASS (all tests)

Run: `dotnet test tests/RockBot.Host.Tests --filter "FullyQualifiedName~AgentContextBuilderSkillRecallFramingTests"`
Expected: PASS (all tests)

- [ ] **Step 8: Run the full solution build and test suite**

Run: `dotnet build RockBot.slnx`
Expected: 0 errors

Run: `dotnet test RockBot.slnx`
Expected: all green

- [ ] **Step 9: Commit**

```bash
git add src/RockBot.Host/IReflexActivationHooks.cs src/RockBot.Tools.Reflex/ReflexToolSurface.cs src/RockBot.Host/AgentContextBuilder.cs tests/RockBot.Host.Tests/AgentContextBuilderSkillRecallFramingTests.cs tests/RockBot.Tools.Reflex.Tests/ReflexToolSurfaceTests.cs
git commit -m "fix: thread DefinitionHash through reflex tool-name activation lookup"
```
