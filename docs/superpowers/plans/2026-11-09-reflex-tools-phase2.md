# Reflex Tools Phase 2: ReflexToolCatalog and ReflexExecutor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a validated, `Wisp`-typed `SkillResource` directly callable as a zero-argument tool (`reflex__{skill}__{filename}`), registered/unregistered automatically as resources validate, demote, or delete, with replay going through the existing wisp runner so Phase 1's repair/validation read-path keeps working unchanged.

**Architecture:** `ReflexToolCatalog` is a plain singleton holding an immutable, `volatile`-swapped `toolName -> (skillName, filename)` map, rebuilt each reconciliation cycle by diffing `ISkillStore.ListAsync()` against what's currently registered in `IToolRegistry` — the same diff-and-swap shape `McpWrapperCatalog` already uses for MCP tools (`_byName`/`_registry.Register`/`.Unregister`), just timer-triggered instead of message-triggered. One shared `ReflexExecutor : IToolExecutor` handles every reflex call by looking up the map, fetching the resource body fresh, and replaying it through a newly extracted `WispReplayPipeline` (canonicalize → hash → circuit-breaker check → execute), the same pipeline `spawn_wisps` uses, so it logs the identical `WispExecutionRecord` shape Phase 1 depends on.

**Tech Stack:** .NET 10, MSTest, hand-written fakes / real file-backed stores (this repo's actual test convention — see `tests/RockBot.Host.Tests/*`, not the Rocks/Moq convention `CLAUDE.md` describes for `RockBot.Messaging.Tests`).

**Spec:** `design/reflex-tools.md`, Phase 2 section (the "Where"/"Change"/"Acceptance" blocks — corrected 2026-11-09 against the verified signatures below; earlier drafts of that section assumed things that don't exist in the codebase, listed explicitly in its "Change" bullets).

## Global Constraints

- No schema/arguments on a reflex call in this phase — `ToolRegistration.ParametersSchema` is an empty-object schema; extending the wisp step format to accept caller-supplied parameters is out of scope (`design/reflex-tools.md` Phase 2, Non-goals-adjacent note).
- No session-level eviction in this phase — `ReflexToolSurface` (Phase 3) doesn't exist yet; a registered reflex is visible to every session immediately, same as `WispToolRegistrar`'s/`DreamCostToolRegistrar`'s static tools.
- No lease/ref-count machinery on `ReflexExecutor` — mirror `McpWrapperCatalog`'s immutable-snapshot-read pattern (read one record out of a `volatile`-swapped dictionary at call start, use that local copy for the rest of the call).
- No new `rockbot.tool.source`/`via=reflex` metric tag — reflex calls are already uniquely identifiable by the `reflex__` name prefix in the existing `rockbot.tool.name` tag; do not inject `IToolRegistry` into `AgentLoopRunner`/`RockBotFunctionInvokingChatClient` for this.
- `"reflex"` becomes a reserved literal server name in `McpServerNames.Validate`, closing the one real naming collision risk.
- Every new/moved method gets an exact signature from this plan — do not invent parameter names or types not listed in a task's Interfaces block.

## Review Focus

- **Two skill names whose sanitized reflex tool names collide** (e.g. a skill literally named `calendar_scan` and a skill named `calendar/scan`, both producing `reflex__calendar_scan__x.json`). A reasonable operator expects one registration failure logged and skipped, not an unhandled exception that kills the whole reconciliation cycle and silently stops every other reflex from ever registering again.
- **A resource whose stored body no longer deserializes as a valid `WispDefinition`** (hand-edited on disk, or a future format change). A reasonable caller expects a clean `ToolInvokeResponse { IsError = true }` naming the problem, not an unhandled exception bubbling out of `ReflexExecutor.ExecuteAsync` into the agent loop.
- **A resource deleted between reconciliation cycles but still registered** (the registered tool's backing file is gone, but the catalog hasn't unregistered it yet). A reasonable caller expects the same clean error as above, not a `NullReferenceException` from assuming `GetResourceAsync` always returns a body for a currently-registered name.
- **A non-Wisp or still-provisional resource on a skill that also has validated Wisp resources.** A reasonable operator expects only the validated Wisp resources to become reflex tools — a `Python` resource or a `Provisional=true` `Wisp` sitting in the same manifest must never register.
- **The circuit breaker trips on a reflex call.** Per the existing `spawn_wisps` contract, a tripped dispatch does no work and is **not** written to the execution log. A reasonable operator expects a reflex call under the same runaway-loop condition to behave identically — return the canned circuit-breaker result as a tool error, skip the `AppendAsync`, not silently log a fabricated success/failure record for work that never ran.

---

## Task 1: Extract the shared wisp-replay pipeline

**Files:**
- Create: `src/RockBot.Wisp/WispReplayPipeline.cs`
- Modify: `src/RockBot.Wisp/SpawnWispsExecutor.cs` (remove the moved private members; call the new class instead — no behavior change)
- Modify: `src/RockBot.Wisp/RockBot.Wisp.csproj` (add `<InternalsVisibleTo Include="RockBot.Tools.Reflex" />` alongside the existing `RockBot.Wisp.Tests` entry)
- Test: `tests/RockBot.Wisp.Tests/WispReplayPipelineTests.cs` (new)
- Test (regression, must stay green with zero edits): `tests/RockBot.Wisp.Tests/SpawnWispsExecutorTests.cs`

**Interfaces:**
- Produces (for Task 3):
  ```csharp
  namespace RockBot.Wisp;

  internal static class WispReplayPipeline
  {
      internal sealed record PreparedRun(
          WispDefinition Definition, string DefinitionJson, string DefinitionHash, string ShapeHash);

      internal static PreparedRun Prepare(WispDefinition definition, IMcpToolDirectory? mcpToolDirectory);

      internal static async Task<(WispExecutionResult Result, bool CircuitBreakerTripped)> RunAsync(
          WispExecutor wispExecutor,
          PreparedRun prepared,
          string wispId,
          string? parentSessionId,
          WispDispatchCircuitBreaker? circuitBreaker,
          ILogger logger,
          CancellationToken ct);
  }
  ```
  `Prepare` canonicalizes shorthand (`McpStepResolver.CanonicalizeShorthand`), serializes, and computes both hashes — the exact sequence currently at `SpawnWispsExecutor.ExecuteOneAsync` lines 112–117. `RunAsync` does the circuit-breaker admit check and, if allowed, calls `wispExecutor.ExecuteAsync(prepared.Definition, wispId, parentSessionId, ct)` — the exact sequence at lines 128–143, including the existing `LogWarning`/`WispDiagnostics.CircuitBreakerTrips.Add(1)` calls on trip. When tripped, returns `(BuildCircuitBreakerResult(wispId, prepared.Definition), true)` and the caller must not log an execution record (see `SpawnWispsExecutor`'s own comment at line ~124: a tripped dispatch is not written to the execution log).

**Change:**
- [ ] **Step 1: Write the failing test**

  In `tests/RockBot.Wisp.Tests/WispReplayPipelineTests.cs`, add a `[TestClass] WispReplayPipelineTests` with:
  ```csharp
  [TestMethod]
  public async Task RunAsync_CircuitBreakerTrips_ReturnsTrippedResult_DoesNotExecute()
  ```
  Build a `WispDispatchCircuitBreaker` already past its limit (same construction pattern `SpawnWispsExecutorTests.cs` already uses for its own circuit-breaker trip test — reuse that exact setup), a minimal valid `WispDefinition`, and a `WispExecutor` constructed so that if `ExecuteAsync` were called the test would fail (e.g. a definition whose steps reference a tool no registered `IToolExecutor` provides, so an unexpected execution throws/returns a distinguishable error). Assert `CircuitBreakerTripped == true` and `Result.IsSuccess == false`.

- [ ] **Step 2: Run test to verify it fails**

  Run: `dotnet test tests/RockBot.Wisp.Tests --filter "FullyQualifiedName~WispReplayPipelineTests"`
  Expected: FAIL — `WispReplayPipeline` does not exist yet (compile error).

- [ ] **Step 3: Move `ComputeDefinitionHash`, `ComputeShapeHash` (and its private `ExtractParamKeys` helper), the private `JsonOptions` field, and `BuildCircuitBreakerResult` from `SpawnWispsExecutor` into `WispReplayPipeline`, all `internal static`. Implement `Prepare`/`RunAsync` as specified above using the moved members. Refactor `SpawnWispsExecutor.ExecuteOneAsync` to call `WispReplayPipeline.Prepare(...)` then `WispReplayPipeline.RunAsync(...)`, and to skip `LogExecutionAsync` entirely when `CircuitBreakerTripped` is true (matching current behavior) instead of returning early before reaching it.**

  One line on the approach: this is a cut of existing code into the new file plus a call-site rewrite, not new logic — every moved method's body is copied verbatim.

- [ ] **Step 4: Run test to verify it passes**

  Run: `dotnet test tests/RockBot.Wisp.Tests --filter "FullyQualifiedName~WispReplayPipelineTests"`
  Expected: PASS (1/1).

- [ ] **Step 5: Run the full existing regression suite unchanged**

  Run: `dotnet test tests/RockBot.Wisp.Tests`
  Expected: PASS, same pass count as before this task (the extraction changed no behavior — `SpawnWispsExecutorTests.cs` is not edited in this task).

- [ ] **Step 6: Commit**

  ```bash
  git add src/RockBot.Wisp/WispReplayPipeline.cs src/RockBot.Wisp/SpawnWispsExecutor.cs src/RockBot.Wisp/RockBot.Wisp.csproj tests/RockBot.Wisp.Tests/WispReplayPipelineTests.cs
  git commit -m "refactor: extract shared wisp-replay pipeline for reflex tools"
  ```

---

## Task 2: Reserve the literal server name "reflex"

**Files:**
- Modify: `src/RockBot.Tools.Mcp/McpServerNames.cs`
- Test: `tests/RockBot.Tools.Tests/McpServerNamesTests.cs` (new)

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces: `McpServerNames.Validate("reflex")` returns a non-null error string. No signature change — same `public static string? Validate(string? name)`.

**Change:**

- [ ] **Step 1: Write the failing test**

  ```csharp
  [TestClass]
  public class McpServerNamesTests
  {
      [TestMethod]
      public void Validate_LiteralReflex_Rejected()
      {
          var error = McpServerNames.Validate("reflex");
          Assert.IsNotNull(error);
      }

      [TestMethod]
      public void Validate_NameContainingReflexAsSubstring_StillAllowed()
      {
          // Only the exact reserved literal is blocked — "myreflexserver" is a normal,
          // otherwise-valid name and must not collide with anything reflex tools name.
          Assert.IsNull(McpServerNames.Validate("myreflexserver"));
      }
  }
  ```

- [ ] **Step 2: Run test to verify it fails**

  Run: `dotnet test tests/RockBot.Tools.Tests --filter "FullyQualifiedName~McpServerNamesTests"`
  Expected: FAIL — `Validate_LiteralReflex_Rejected` fails (`error` is null today).

- [ ] **Step 3: Add one check to `McpServerNames.Validate`**

  Add, after the existing `__`-containment check and before the final `return null;`:
  ```csharp
  if (string.Equals(name, "reflex", StringComparison.OrdinalIgnoreCase))
      return "Server name 'reflex' is reserved for reflex tools and cannot be used.";
  ```

- [ ] **Step 4: Run test to verify it passes**

  Run: `dotnet test tests/RockBot.Tools.Tests --filter "FullyQualifiedName~McpServerNamesTests"`
  Expected: PASS (2/2).

- [ ] **Step 5: Commit**

  ```bash
  git add src/RockBot.Tools.Mcp/McpServerNames.cs tests/RockBot.Tools.Tests/McpServerNamesTests.cs
  git commit -m "feat: reserve the literal MCP server name 'reflex'"
  ```

---

## Task 3: `RockBot.Tools.Reflex` project, tool naming, and `ReflexExecutor`

**Files:**
- Create: `src/RockBot.Tools.Reflex/RockBot.Tools.Reflex.csproj`
- Create: `src/RockBot.Tools.Reflex/ReflexToolNames.cs`
- Create: `src/RockBot.Tools.Reflex/IReflexRegistrationLookup.cs`
- Create: `src/RockBot.Tools.Reflex/ReflexExecutor.cs`
- Create: `tests/RockBot.Tools.Reflex.Tests/RockBot.Tools.Reflex.Tests.csproj`
- Create: `tests/RockBot.Tools.Reflex.Tests/ReflexToolNamesTests.cs`
- Create: `tests/RockBot.Tools.Reflex.Tests/ReflexExecutorTests.cs`
- Modify: `RockBot.slnx` (add both new `<Project Path="..."/>` lines, alongside the existing `RockBot.Tools.Mcp`/`RockBot.Wisp` entries)

**Interfaces:**
- Consumes (from Task 1): `RockBot.Wisp.WispReplayPipeline.Prepare`/`RunAsync` exactly as declared there (requires the `InternalsVisibleTo` added in Task 1, Step 3).
- Consumes (pre-existing, verified): `RockBot.Tools.IToolExecutor.ExecuteAsync(ToolInvokeRequest, CancellationToken) : Task<ToolInvokeResponse>`; `RockBot.Tools.ToolInvokeRequest { ToolCallId, ToolName, Arguments, SessionId, BatchId }`; `RockBot.Tools.ToolInvokeResponse { ToolCallId, ToolName, Content, ContentBlocks, IsError }`; `RockBot.Host.ISkillStore.GetResourceAsync(string skillName, string filename) : Task<string?>`; `RockBot.Wisp.WispExecutor.ExecuteAsync` (via the pipeline, not called directly); `RockBot.Host.WispExecutionRecord` (all fields as in `src/RockBot.Host.Abstractions/WispExecutionRecord.cs`); `RockBot.Host.IWispExecutionLog.AppendAsync(WispExecutionRecord, CancellationToken)`.
- Produces (for Task 4):
  ```csharp
  namespace RockBot.Tools.Reflex;

  public static class ReflexToolNames
  {
      public const string Prefix = "reflex";
      public const string Separator = "__";

      /// Builds the registered tool name for a (skillName, filename) pair, sanitizing
      /// any character outside [A-Za-z0-9_.-] (skill names may contain '/') to '_'.
      public static string Build(string skillName, string filename);
  }

  internal interface IReflexRegistrationLookup
  {
      bool TryGet(string toolName, out string skillName, out string filename);
  }

  public sealed class ReflexExecutor(
      IReflexRegistrationLookup lookup,
      RockBot.Host.ISkillStore skillStore,
      RockBot.Wisp.WispExecutor wispExecutor,
      RockBot.Host.IWispExecutionLog? executionLog,
      RockBot.Wisp.WispDispatchCircuitBreaker? circuitBreaker,
      RockBot.Tools.IMcpToolDirectory? mcpToolDirectory,
      ILogger<ReflexExecutor> logger) : IToolExecutor
  {
      public Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct);
  }
  ```

**Change:**

- [ ] **Step 1: Write the failing tests for `ReflexToolNames.Build`**

  ```csharp
  [TestClass]
  public class ReflexToolNamesTests
  {
      [TestMethod]
      public void Build_SimpleSkillAndFilename_UsesPrefixAndSeparator()
      {
          Assert.AreEqual("reflex__calendar__fanout.json", ReflexToolNames.Build("calendar", "fanout.json"));
      }

      [TestMethod]
      public void Build_SkillNameWithSlash_SanitizesSlashToUnderscore()
      {
          Assert.AreEqual("reflex__calendar_scan__fanout.json", ReflexToolNames.Build("calendar/scan", "fanout.json"));
      }
  }
  ```

- [ ] **Step 2: Run test to verify it fails**

  Run: `dotnet test tests/RockBot.Tools.Reflex.Tests --filter "FullyQualifiedName~ReflexToolNamesTests"`
  Expected: FAIL — project/class does not exist yet.

  First scaffold `RockBot.Tools.Reflex.csproj` (mirror `src/RockBot.Tools.Mcp/RockBot.Tools.Mcp.csproj`'s shape: `TargetFramework net10.0`, `ImplicitUsings`/`Nullable` enabled, `RootNamespace RockBot.Tools.Reflex`, `IsPackable true`; `<InternalsVisibleTo Include="RockBot.Tools.Reflex.Tests" />`; package refs `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Hosting.Abstractions`, `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.Options`, all `Version="10.0.*"`; project refs `RockBot.Tools.Abstractions`, `RockBot.Host` (the full project — transitively carries `RockBot.Host.Abstractions`'s `ISkillStore`/`SkillResource`/`IWispExecutionLog`), `RockBot.Wisp`), and `RockBot.Tools.Reflex.Tests.csproj` (mirror `tests/RockBot.Tools.Tests/RockBot.Tools.Tests.csproj`'s shape plus a reference to the new `RockBot.Tools.Reflex.csproj`). Add both to `RockBot.slnx`.

- [ ] **Step 3: Implement `ReflexToolNames.Build`**

  `Regex.Replace` any character outside `[A-Za-z0-9_.-]` to `_`, applied to `skillName` and `filename` independently, then join as `$"{Prefix}{Separator}{sanitizedSkill}{Separator}{sanitizedFilename}"`.

- [ ] **Step 4: Run test to verify it passes**

  Run: `dotnet test tests/RockBot.Tools.Reflex.Tests --filter "FullyQualifiedName~ReflexToolNamesTests"`
  Expected: PASS (2/2).

- [ ] **Step 5: Write the failing tests for `ReflexExecutor`**

  Add an in-memory `ISkillStore` fake (reuse `InMemorySkillResourceStore` from `tests/RockBot.Host.Tests/SkillResourceApplierTests.cs` as reference for shape, or write a minimal local one implementing only `GetResourceAsync`/`AttachResourceAsync` needed here) and a trivial `IReflexRegistrationLookup` test double. A real `WispExecutor` needs a real `IToolRegistry` with at least one registered no-op tool so a minimal wisp definition (one `direct` step calling that tool) actually succeeds — mirror how `tests/RockBot.Wisp.Tests/SpawnWispsExecutorTests.cs` constructs its own `WispExecutor` test fixture (same helper/pattern, reused here, not reinvented).

  ```csharp
  [TestMethod]
  public async Task ExecuteAsync_ValidatedResource_ReplaysWispAndAppendsExecutionRecord()
  ```
  Arrange: lookup returns `("calendar/scan", "fanout.json")` for `"reflex__calendar_scan__fanout.json"`; skill store's `GetResourceAsync("calendar/scan", "fanout.json")` returns a valid one-step `WispDefinition` JSON body that succeeds against the test fixture's registry. Act: call `ExecuteAsync` with a `ToolInvokeRequest { ToolName = "reflex__calendar_scan__fanout.json", ... }`. Assert: `IsError == false`; the fake `IWispExecutionLog`'s appended record has `Succeeded == true` and a non-empty `DefinitionHash`.

  ```csharp
  [TestMethod]
  public async Task ExecuteAsync_LookupMisses_ReturnsErrorResponse_NoThrow()
  ```
  Lookup returns `false`. Assert `IsError == true`, no exception.

  ```csharp
  [TestMethod]
  public async Task ExecuteAsync_ResourceBodyMissing_ReturnsErrorResponse_NoThrow()
  ```
  Lookup succeeds; `GetResourceAsync` returns `null` (deleted between reconciliation cycles). Assert `IsError == true`, no exception. (Review Focus item 3.)

  ```csharp
  [TestMethod]
  public async Task ExecuteAsync_StoredBodyNotValidWispJson_ReturnsErrorResponse_NoThrow()
  ```
  `GetResourceAsync` returns `"not valid json"`. Assert `IsError == true`, no exception. (Review Focus item 2.)

  ```csharp
  [TestMethod]
  public async Task ExecuteAsync_CircuitBreakerTripped_ReturnsErrorResponse_DoesNotAppendExecutionRecord()
  ```
  A `WispDispatchCircuitBreaker` already past its limit for the resource's definition hash. Assert `IsError == true` and the fake `IWispExecutionLog.AppendAsync` was never called. (Review Focus item 5.)

- [ ] **Step 6: Run tests to verify they fail**

  Run: `dotnet test tests/RockBot.Tools.Reflex.Tests --filter "FullyQualifiedName~ReflexExecutorTests"`
  Expected: FAIL — `ReflexExecutor` does not exist yet.

- [ ] **Step 7: Implement `ReflexExecutor.ExecuteAsync`**

  Sequence: `lookup.TryGet` → on miss, return an error `ToolInvokeResponse`; `skillStore.GetResourceAsync(skillName, filename)` → on `null`, return an error response; `JsonSerializer.Deserialize<WispDefinition>(body)` in a `try`/`catch (JsonException)` → on failure, return an error response; `WispReplayPipeline.Prepare` then `RunAsync` (new `wispId` per call, `request.SessionId` as `parentSessionId`); when `CircuitBreakerTripped`, return an error response and skip logging entirely; otherwise build a `WispExecutionRecord` from the result (same field mapping `SpawnWispsExecutor.LogExecutionAsync` uses at lines 222–244, minus `RetryOf`/`BatchId`, which don't apply to a reflex replay — leave both `null`) and call `executionLog?.AppendAsync`; return a `ToolInvokeResponse` with `IsError = !result.IsSuccess` and `Content` summarizing success or the failed step's error message.

- [ ] **Step 8: Run tests to verify they pass**

  Run: `dotnet test tests/RockBot.Tools.Reflex.Tests --filter "FullyQualifiedName~ReflexExecutorTests"`
  Expected: PASS (5/5).

- [ ] **Step 9: Commit**

  ```bash
  git add src/RockBot.Tools.Reflex tests/RockBot.Tools.Reflex.Tests RockBot.slnx
  git commit -m "feat: add RockBot.Tools.Reflex project with ReflexToolNames and ReflexExecutor"
  ```

---

## Task 4: `ReflexToolCatalog`, `ReflexOptions`, and DI wiring

**Files:**
- Create: `src/RockBot.Tools.Reflex/ReflexOptions.cs`
- Create: `src/RockBot.Tools.Reflex/ReflexToolCatalog.cs`
- Create: `src/RockBot.Tools.Reflex/ReflexServiceCollectionExtensions.cs`
- Modify: `src/RockBot.Agent/Program.cs` (call the new extension method, alongside the existing `agent.AddMcpToolProxy(...)` call at line ~384)
- Test: `tests/RockBot.Tools.Reflex.Tests/ReflexToolCatalogTests.cs` (new)

**Interfaces:**
- Consumes (from Task 3): `ReflexToolNames.Build`, `IReflexRegistrationLookup`, `ReflexExecutor`.
- Consumes (pre-existing, verified): `RockBot.Tools.IToolRegistry.Register(ToolRegistration, IToolExecutor)` (throws `InvalidOperationException` on name collision — catch and log, don't let one bad entry kill the cycle), `.Unregister(string) : bool`, `.GetTools() : IReadOnlyList<ToolRegistration>`; `RockBot.Host.ISkillStore.ListAsync() : Task<IReadOnlyList<Skill>>`; `RockBot.Host.Skill.Manifest : IReadOnlyList<SkillResource>?`; `RockBot.Host.SkillResource { Filename, Type, Provisional, ... }`; `RockBot.Host.SkillResourceType.Wisp`; `RockBot.Host.AgentHostBuilder` (extended by `ReflexServiceCollectionExtensions`, same shape `McpServiceCollectionExtensions.AddMcpToolProxy` uses: `builder.Services`).
- Produces: nothing further — this is the last task in this phase.

**Change:**

- [ ] **Step 1: Write the failing test for reconciliation registering a validated resource**

  ```csharp
  [TestClass]
  public class ReflexToolCatalogTests
  {
      [TestMethod]
      public async Task ReconcileOnceAsync_ValidatedWispResource_RegistersReflexTool()
  ```
  Arrange: an in-memory `ISkillStore` fake (new local one in this file, or reuse Task 3's) returning one skill with one `Manifest` entry `{ Filename = "fanout.json", Type = SkillResourceType.Wisp, Provisional = false }`; a real `ToolRegistry` (the concrete `RockBot.Tools.ToolRegistry`, via `new ToolRegistry()` if its constructor is accessible, or `IToolRegistry` from a minimal DI container — check which is simpler at implementation time) as `IToolRegistry`. Act: construct `ReflexToolCatalog(skillStore, registry, Options.Create(new ReflexOptions()), NullLogger<ReflexToolCatalog>.Instance)` and call `ReconcileOnceAsync(CancellationToken.None)`. Assert: `registry.GetTools()` contains one entry named `ReflexToolNames.Build("calendar/scan", "fanout.json")`, and `((IReflexRegistrationLookup)catalog).TryGet(thatName, out var skillName, out var filename)` returns `true` with the right skill/filename.

  ```csharp
      [TestMethod]
      public async Task ReconcileOnceAsync_ProvisionalOrNonWispResource_NeverRegistered()
  ```
  Same skill, but one manifest entry `Provisional = true` (still `Wisp`) and one `Type = SkillResourceType.Python, Provisional = false`. Assert `registry.GetTools()` is empty after `ReconcileOnceAsync`. (Review Focus item 4.)

  ```csharp
      [TestMethod]
      public async Task ReconcileOnceAsync_ResourceDemotedSinceLastCycle_UnregistersTool()
  ```
  First cycle with a validated resource (asserts registered, as above). Mutate the fake store's skill so the same resource is now `Provisional = true` (simulating Phase 1's retrain path). Second `ReconcileOnceAsync` call. Assert `registry.GetTools()` is empty and the lookup's `TryGet` for that name now returns `false`.

  ```csharp
      [TestMethod]
      public async Task ReconcileOnceAsync_TwoSkillsSanitizeToSameToolName_SkipsSecondWithoutThrowing()
  ```
  Two skills (`"calendar_scan"` and `"calendar/scan"`) each with one validated resource named `"x.json"`, producing the same sanitized tool name. Assert `ReconcileOnceAsync` does not throw and exactly one of the two ends up registered. (Review Focus item 1.)

- [ ] **Step 2: Run tests to verify they fail**

  Run: `dotnet test tests/RockBot.Tools.Reflex.Tests --filter "FullyQualifiedName~ReflexToolCatalogTests"`
  Expected: FAIL — `ReflexToolCatalog`/`ReflexOptions` do not exist yet.

- [ ] **Step 3: Implement `ReflexOptions`**

  ```csharp
  namespace RockBot.Tools.Reflex;

  public sealed class ReflexOptions
  {
      /// Whether the reconciliation loop runs at all. Default true.
      public bool Enabled { get; set; } = true;

      /// How often ReflexToolCatalog re-scans ISkillStore for validated Wisp resources
      /// to register or unregister. Default 60 seconds.
      public TimeSpan ReconciliationInterval { get; set; } = TimeSpan.FromSeconds(60);
  }
  ```

- [ ] **Step 4: Implement `ReflexToolCatalog`**

  ```csharp
  public sealed class ReflexToolCatalog(
      ISkillStore skillStore,
      IToolRegistry registry,
      IOptions<ReflexOptions> options,
      ILogger<ReflexToolCatalog> logger) : IReflexRegistrationLookup
  {
      public async Task ReconcileOnceAsync(CancellationToken ct);
      bool IReflexRegistrationLookup.TryGet(string toolName, out string skillName, out string filename);
  }
  ```
  Internals: `private readonly object _gate = new();` plus `private volatile IReadOnlyDictionary<string, (string SkillName, string Filename)> _byName = new Dictionary<...>();` — same shape as `McpWrapperCatalog._byName`. `ReconcileOnceAsync` builds a fresh `desired` dictionary by listing skills, scanning each `Manifest` for `Type == SkillResourceType.Wisp && !Provisional`, computing `ReflexToolNames.Build(skill.Name, resource.Filename)` per entry and skipping (with a `LogWarning`) a name already claimed by a different `(skillName, filename)` pair this cycle; under `_gate`, unregisters every name in the old `_byName` not in `desired` (via `registry.Unregister`), registers every name in `desired` not in the old `_byName` (via `registry.Register(new ToolRegistration { Name = name, Description = ..., ParametersSchema = "{}", Source = "reflex" }, sharedExecutor)`, catching and logging `InvalidOperationException` per entry rather than aborting the cycle), then swaps `_byName = desired`. `TryGet` reads `_byName` lock-free, exactly like `McpWrapperCatalog`'s own lookups.

  Note: this task does not yet construct the real `ReflexExecutor` to pass to `Register` — accept it as a constructor parameter (`IToolExecutor sharedExecutor`) so the catalog's own tests (which only care about registration bookkeeping) can pass a trivial stub executor, and the DI wiring in Step 6 constructs the real `ReflexExecutor` (itself depending on this same catalog as its `IReflexRegistrationLookup`) and passes it in.

- [ ] **Step 5: Run tests to verify they pass**

  Run: `dotnet test tests/RockBot.Tools.Reflex.Tests --filter "FullyQualifiedName~ReflexToolCatalogTests"`
  Expected: PASS (4/4).

- [ ] **Step 6: Implement `ReflexServiceCollectionExtensions.AddReflexTools` and wire it into `RockBot.Agent/Program.cs`**

  ```csharp
  namespace RockBot.Tools.Reflex;

  public static class ReflexServiceCollectionExtensions
  {
      public static AgentHostBuilder AddReflexTools(
          this AgentHostBuilder builder, Action<ReflexOptions>? configure = null);
  }
  ```
  Mirrors `McpServiceCollectionExtensions.AddMcpToolProxy`'s shape: registers `IOptions<ReflexOptions>` (applying `configure` if given), `ReflexToolCatalog` as a singleton also exposed as `IReflexRegistrationLookup` (same instance — register the concrete type once, then `TryAddSingleton<IReflexRegistrationLookup>(sp => sp.GetRequiredService<ReflexToolCatalog>())`), `ReflexExecutor` as a singleton (consuming that same `IReflexRegistrationLookup`), and a small internal `IHostedService` (`ReflexToolRegistrar`, a `PeriodicTimer`-driven loop at `options.ReconciliationInterval` calling `catalog.ReconcileOnceAsync`, each tick's exception caught and logged so one bad cycle doesn't stop the timer) registered via `builder.Services.AddHostedService<ReflexToolRegistrar>()` — skip registering the hosted service entirely when `options.Enabled == false`. In `src/RockBot.Agent/Program.cs`, add `agent.AddReflexTools();` next to the existing `agent.AddMcpToolProxy(...)` call.

- [ ] **Step 7: Run the build to verify the DI wiring compiles and the whole solution is unaffected**

  Run: `dotnet build RockBot.slnx`
  Expected: 0 errors.

- [ ] **Step 8: Run the full `RockBot.Tools.Reflex.Tests` and `RockBot.Wisp.Tests` suites**

  Run: `dotnet test tests/RockBot.Tools.Reflex.Tests && dotnet test tests/RockBot.Wisp.Tests`
  Expected: both PASS, full counts.

- [ ] **Step 9: Commit**

  ```bash
  git add src/RockBot.Tools.Reflex src/RockBot.Agent/Program.cs
  git commit -m "feat: add ReflexToolCatalog reconciliation loop and wire reflex tools into the agent host"
  ```

## Verification

- `dotnet build RockBot.slnx` — 0 errors.
- `dotnet test RockBot.slnx` (unit only, no RabbitMQ needed) — full solution green, same or higher pass count than before this plan.
- Manual spot-check once deployed (not part of this plan's automated tests, per the design doc's own "Acceptance" wording — "within one reconciliation cycle"): promote a real wisp to validated, wait up to `ReflexOptions.ReconciliationInterval` (default 60s), confirm `reflex__{skill}__{filename}` appears as a callable tool; call it directly; demote or delete the underlying resource and confirm the tool disappears within one more interval.
