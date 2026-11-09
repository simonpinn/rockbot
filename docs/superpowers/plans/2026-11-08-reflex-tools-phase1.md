# Reflex Tools Phase 1: Retrain Before Delete — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Redirect skill-resource failure handling through the existing self-repair ticket loop instead of deleting a provisional resource on its second consecutive failure, so a resource gets a chance to retrain before it's removed.

**Architecture:** `RunProvisionalValidationPassAsync`'s `Remove` case stops calling `RemoveResourceAsync` and instead records the failure into `FailureClusterStore` using the real session ids from the failing wisp records — fixing a latent bug where it always recorded `sessionId: null`, which would otherwise leave a redirected resource unreachable by the repair pipeline forever (`FileFailureClusterStore` only counts non-empty session ids toward the 2-distinct-session escalation threshold). Once a cluster genuinely crosses that existing threshold, the already-generic repair-ticket creation pass needs to recognize `server="skill-resource"` clusters and propose a `RepairTicket(Target=SkillResource)` — which requires extending `BuiltInRepairTicketCreationDirective`'s prompt text and `BuildRepairTicketCreationPrompt`'s cluster context with prior-attempt history, not any new pass or directive file. The apply/verify/escalate loop (`RunRepairTicketApplyAsync`) and `SkillResourceApplier` need no changes at all — both are already fully generic and already DI-registered.

**Tech Stack:** .NET 10, MSTest (`[TestClass]`/`[TestMethod]`), hand-written in-memory and real-file-backed-against-a-temp-dir test fakes — **not** Rocks or Moq, despite the root `CLAUDE.md`'s claim; confirmed against `tests/RockBot.Host.Tests/ProvisionalValidationPassTests.cs`.

**Spec:** `design/reflex-tools.md` Phase 1, and the Amendment 1 section of `design/skill-asset-promotion.md`.

## Global Constraints

- No change to `SkillResourceApplier`, `RunRepairTicketApplyAsync`, or `RepairTicketOptions.MaxAttempts` (default 3) — all already correct and generic.
- No change to `DecideProvisionalAction`'s decision logic — only to what the caller does with a `Remove` decision.
- Tool-registration work (`reflex-tools.md` Phase 2+) is out of scope for this plan.
- Match this test project's existing convention exactly: MSTest, hand-written fakes or a real file-backed store against a temp directory — never Rocks or Moq.

## Review Focus

- A resource whose recent failing records mix real and empty/missing session ids must still produce a usable, correctly-keyed cluster rather than throwing or silently recording nothing. Owned by Task 1.
- Recording the same still-failing resource's failure across repeated dream cycles must not misbehave (throw, duplicate the cluster key, or lose earlier session ids) — it should simply keep adding any genuinely new session ids to the same cluster. Owned by Task 1.
- A cluster with no prior repair-ticket history must not be shown a fabricated "no prior attempts" line that a real LLM could misread as evidence either way — the absence of history must be genuinely absent from the prompt. Owned by Task 2.

---

### Task 1: Stop deleting on `Remove`; record real session ids

**Files:**
- Modify: `src/RockBot.Host/DreamService.cs` — the `ProvisionalAction.Remove` case inside `RunProvisionalValidationPassAsync` (~line 5591-5618); add one new method near `DecideProvisionalAction` (~line 5659).
- Test: `tests/RockBot.Host.Tests/ProvisionalValidationPassTests.cs`.

**Interfaces:**
- Consumes: `IFailureClusterStore.RecordAsync(ClusterKey, string? sessionId, string errorMessage, DateTimeOffset at, CancellationToken ct)` (existing, unchanged); `WispExecutionRecord.SessionId` (existing field); `ProvisionalDecision.ConsecutiveFailureCount` (existing field, already populated by `DecideProvisionalAction`).
- Produces: `internal static Task RecordProvisionalFailureAsync(IFailureClusterStore failureClusterStore, string skillName, SkillResource resource, IReadOnlyList<WispExecutionRecord> wispRecords, int consecutiveFailureCount, DateTimeOffset now, CancellationToken ct)`. No other task in this plan calls it besides `RunProvisionalValidationPassAsync` itself.

- [ ] **Step 1: Write the failing tests**

```csharp
[TestMethod]
public async Task RecordProvisionalFailure_TwoDistinctSessionFailures_ClusterHasBothSessionIds()
{
    var opts = Microsoft.Extensions.Options.Options.Create(new FailureClusterOptions());
    var store = new FileFailureClusterStore(opts, NullLogger<FileFailureClusterStore>.Instance);
    try
    {
        await ((IHostedService)store).StartAsync(default);
        var resource = ProvWisp(); // existing helper in this file
        var records = new[]
        {
            WispRecord(false, "s2", Now.AddHours(-2)),
            WispRecord(false, "s3", Now.AddHours(-1)),
        };

        await DreamService.RecordProvisionalFailureAsync(
            store, "calendar/scan", resource, records, consecutiveFailureCount: 2, Now, default);

        var clusters = await store.GetAllAsync();
        var cluster = clusters.Single(c => c.Key.Tool == "calendar/scan" && c.Key.ErrorClass == resource.Filename);
        CollectionAssert.AreEquivalent(new[] { "s2", "s3" }, cluster.SessionIds.ToList());
        Assert.AreEqual("skill-resource", cluster.Key.Server);
    }
    finally { await ((IAsyncDisposable)store).DisposeAsync(); }
}

[TestMethod]
public async Task RecordProvisionalFailure_MixedRealAndEmptySessionIds_RecordsOnlyRealOnes()
{
    var opts = Microsoft.Extensions.Options.Options.Create(new FailureClusterOptions());
    var store = new FileFailureClusterStore(opts, NullLogger<FileFailureClusterStore>.Instance);
    try
    {
        await ((IHostedService)store).StartAsync(default);
        var resource = ProvWisp();
        var records = new[]
        {
            WispRecord(false, "", Now.AddHours(-3)),
            WispRecord(false, "s2", Now.AddHours(-1)),
        };

        await DreamService.RecordProvisionalFailureAsync(
            store, "calendar/scan", resource, records, consecutiveFailureCount: 2, Now, default);

        var cluster = (await store.GetAllAsync())
            .Single(c => c.Key.Tool == "calendar/scan" && c.Key.ErrorClass == resource.Filename);
        CollectionAssert.AreEquivalent(new[] { "s2" }, cluster.SessionIds.ToList());
    }
    finally { await ((IAsyncDisposable)store).DisposeAsync(); }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test RockBot.slnx --filter "FullyQualifiedName~RecordProvisionalFailure_TwoDistinctSessionFailures_ClusterHasBothSessionIds|FullyQualifiedName~RecordProvisionalFailure_MixedRealAndEmptySessionIds_RecordsOnlyRealOnes"`
Expected: FAIL — `DreamService.RecordProvisionalFailureAsync` does not exist (compile error).

- [ ] **Step 3: Implement `RecordProvisionalFailureAsync` in `src/RockBot.Host/DreamService.cs`**

Add near `DecideProvisionalAction`:

```csharp
internal static async Task RecordProvisionalFailureAsync(
    IFailureClusterStore failureClusterStore,
    string skillName,
    SkillResource resource,
    IReadOnlyList<WispExecutionRecord> wispRecords,
    int consecutiveFailureCount,
    DateTimeOffset now,
    CancellationToken ct)
```

Build `var clusterKey = new ClusterKey(Server: "skill-resource", Tool: skillName, ErrorClass: resource.Filename);` (same shape the current code already uses). Among `wispRecords` where `!r.Succeeded`, collect the distinct non-empty `SessionId` values and call `failureClusterStore.RecordAsync(clusterKey, sessionId, $"provisional resource failing after {consecutiveFailureCount} consecutive failures", now, ct)` once per distinct id. If none of the failing records carry a non-empty `SessionId`, make exactly one call with `sessionId: null` instead, so a cluster still exists for visibility (logged at Debug) — this keeps the method total even on the defensive edge case without ever inventing a session id.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test RockBot.slnx --filter "FullyQualifiedName~RecordProvisionalFailure_TwoDistinctSessionFailures_ClusterHasBothSessionIds|FullyQualifiedName~RecordProvisionalFailure_MixedRealAndEmptySessionIds_RecordsOnlyRealOnes"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/RockBot.Host/DreamService.cs tests/RockBot.Host.Tests/ProvisionalValidationPassTests.cs
git commit -m "feat: record real session ids for provisional-resource failure clusters"
```

- [ ] **Step 6: Write the failing test for the caller change**

```csharp
[TestMethod]
public async Task RecordProvisionalFailure_SameResourceRecordedTwice_AccumulatesNewSessionIds()
{
    var opts = Microsoft.Extensions.Options.Options.Create(new FailureClusterOptions());
    var store = new FileFailureClusterStore(opts, NullLogger<FileFailureClusterStore>.Instance);
    try
    {
        await ((IHostedService)store).StartAsync(default);
        var resource = ProvWisp();

        await DreamService.RecordProvisionalFailureAsync(
            store, "calendar/scan", resource, [WispRecord(false, "s2", Now.AddHours(-2))], 2, Now, default);
        await DreamService.RecordProvisionalFailureAsync(
            store, "calendar/scan", resource, [WispRecord(false, "s3", Now.AddHours(-1))], 2, Now.AddHours(1), default);

        var cluster = (await store.GetAllAsync())
            .Single(c => c.Key.Tool == "calendar/scan" && c.Key.ErrorClass == resource.Filename);
        CollectionAssert.AreEquivalent(new[] { "s2", "s3" }, cluster.SessionIds.ToList());
        Assert.AreEqual(2, cluster.Count);
    }
    finally { await ((IAsyncDisposable)store).DisposeAsync(); }
}
```

This test should already pass once Step 3 lands (it exercises `RecordProvisionalFailureAsync` directly across two calls, the same way two dream cycles would) — it is written here, not Step 1, because it documents the cross-cycle accumulation behavior the caller change in Step 8 depends on, and belongs next to that change for anyone reading the task later.

- [ ] **Step 7: Run it to confirm it already passes**

Run: `dotnet test RockBot.slnx --filter "FullyQualifiedName~RecordProvisionalFailure_SameResourceRecordedTwice_AccumulatesNewSessionIds"`
Expected: PASS (confirms Step 3's implementation already satisfies this; if it fails, the cause is a bug in Step 3, not a missing step — fix there via superpowers:systematic-debugging before continuing).

- [ ] **Step 8: Change the `Remove` case in `RunProvisionalValidationPassAsync`**

Replace the body of `case ProvisionalAction.Remove:` (currently: call `_skillStore.RemoveResourceAsync`, then on success record a cluster with `sessionId: null`) with a call to `RecordProvisionalFailureAsync(_failureClusterStore, skillName, resource, wispRecords, decision.ConsecutiveFailureCount, now, ct)`, guarded by `_failureClusterStore is not null` the same way the surrounding code already guards it. Rename the `removed` counter to `flaggedForRepair` and update the final summary log line from `"{Promoted} promoted, {Removed} removed, {Stale} marked stale"` to `"{Promoted} promoted, {FlaggedForRepair} flagged for repair, {Stale} marked stale"`. Update the per-iteration log message from `"removed '{File}' on '{Skill}'"` to `"flagged '{File}' on '{Skill}' for repair"`.

- [ ] **Step 9: Run the whole task's test file**

Run: `dotnet test RockBot.slnx --filter "FullyQualifiedName~ProvisionalValidationPassTests"`
Expected: PASS — all tests, including the pre-existing `Decide_*` tests (untouched) and the three new tests from this task.

- [ ] **Step 10: Commit**

```bash
git add src/RockBot.Host/DreamService.cs tests/RockBot.Host.Tests/ProvisionalValidationPassTests.cs
git commit -m "feat: stop deleting provisional resources directly on Remove decision"
```

---

### Task 2: Teach the repair-ticket creation pass about `SkillResource`

**Files:**
- Modify: `src/RockBot.Host/DreamService.cs` — `BuiltInRepairTicketCreationDirective` (~line 8015), `BuildRepairTicketCreationPrompt` (~line 7883), its one call site (~line 7576).
- Test: `tests/RockBot.Host.Tests/RepairTicketCreationPromptTests.cs` (new file — no existing test covers `BuildRepairTicketCreationPrompt` directly; follow `ProvisionalValidationPassTests.cs`'s MSTest style and `Now` convention).

**Interfaces:**
- Consumes: `FailureCluster` (existing: `Key`, `Count`, `SessionIds`, `FirstSeen`, `LastSeen`, `SampleErrorMessages`); `RepairTicket` (existing: `PatternKey`, `Target`, `Change`, `Status`); the `byPattern` dictionary `RunRepairTicketCreationPassAsync` already builds at its existing call site (grouped `existing` tickets by `PatternKey`, `Dictionary<string, List<RepairTicket>>`).
- Produces: `internal static string BuildRepairTicketCreationPrompt(IEnumerable<FailureCluster> clusters, IReadOnlyDictionary<string, List<RepairTicket>> priorTicketsByPattern)` — signature change (new second parameter, and accessibility raised from `private` to `internal` so tests can call it directly, matching the project's existing precedent of `internal static` test-visible pure helpers such as `DecideProvisionalAction`). No other task in this plan consumes it.

- [ ] **Step 1: Write the failing tests**

```csharp
namespace RockBot.Host.Tests;

[TestClass]
public class RepairTicketCreationPromptTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 9, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void BuildPrompt_ClusterWithEscalatedPriorTicket_IncludesPriorAttemptNote()
    {
        var cluster = new FailureCluster(
            Key: new ClusterKey("skill-resource", "calendar/scan", "fanout.json"),
            Count: 4,
            SessionIds: new HashSet<string> { "s2", "s3" },
            FirstSeen: Now.AddHours(-5),
            LastSeen: Now,
            SampleErrorMessages: new List<string> { "tool fingerprint changed" });

        var priorTicket = new RepairTicket(
            Id: "ticket-abc123",
            PatternKey: "skill-resource|calendar/scan|fanout.json",
            Target: RepairTarget.SkillResource,
            Change: JsonDocument.Parse(
                """{"skill":"calendar/scan","filename":"fanout.json","op":"demote-provisional"}""").RootElement,
            Verify: new VerifyShape("calendar-mcp", "get_events", JsonDocument.Parse("{}").RootElement,
                new VerifyExpectation(VerifyExpectationKind.Success, null)),
            Attempts: [],
            Status: RepairStatus.Escalated,
            CreatedAt: Now.AddDays(-1),
            UpdatedAt: Now.AddHours(-1));

        var priorByPattern = new Dictionary<string, List<RepairTicket>>
        {
            ["skill-resource|calendar/scan|fanout.json"] = [priorTicket],
        };

        var prompt = DreamService.BuildRepairTicketCreationPrompt([cluster], priorByPattern);

        StringAssert.Contains(prompt, "demote-provisional");
        StringAssert.Contains(prompt, "Escalated");
    }

    [TestMethod]
    public void BuildPrompt_ClusterWithNoPriorTickets_OmitsPriorAttemptNote()
    {
        var cluster = new FailureCluster(
            Key: new ClusterKey("skill-resource", "calendar/scan", "fanout.json"),
            Count: 3,
            SessionIds: new HashSet<string> { "s1", "s2" },
            FirstSeen: Now.AddHours(-2),
            LastSeen: Now,
            SampleErrorMessages: new List<string> { "boom" });

        var prompt = DreamService.BuildRepairTicketCreationPrompt([cluster], new Dictionary<string, List<RepairTicket>>());

        Assert.IsFalse(prompt.Contains("prior ticket", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(prompt.Contains("no prior", StringComparison.OrdinalIgnoreCase));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test RockBot.slnx --filter "FullyQualifiedName~RepairTicketCreationPromptTests"`
Expected: FAIL — compile error (`BuildRepairTicketCreationPrompt` is `private`, takes one parameter, or both).

- [ ] **Step 3: Change the signature and add the prior-ticket note**

Change `private static string BuildRepairTicketCreationPrompt(IEnumerable<FailureCluster> clusters)` to `internal static string BuildRepairTicketCreationPrompt(IEnumerable<FailureCluster> clusters, IReadOnlyDictionary<string, List<RepairTicket>> priorTicketsByPattern)`. After each cluster's existing lines (server/tool/errorClass/count/sessions/firstSeen/lastSeen, then up to 3 sample errors), if `priorTicketsByPattern.TryGetValue(SerializeClusterKey(c.Key), out var priors)` and `priors.Count > 0`, append one line per prior ticket: extract the `op` field from `ticket.Change` when present (fall back to the target name alone when absent) and print `"   prior attempt: {Target} {op} -> {Status}"`. Print nothing at all for a cluster with no prior tickets — no placeholder line.

- [ ] **Step 4: Update the one call site**

At `RunRepairTicketCreationPassAsync` (~line 7576), it already computes `byPattern` one line earlier than today's `BuildRepairTicketCreationPrompt(newClusters)` call — change that call to `BuildRepairTicketCreationPrompt(newClusters, byPattern)`.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test RockBot.slnx --filter "FullyQualifiedName~RepairTicketCreationPromptTests"`
Expected: PASS

- [ ] **Step 6: Extend `BuiltInRepairTicketCreationDirective` with the `SkillResource` target block**

In the `"""`-delimited constant (~line 8015), after the existing `ToolDefaultRegister`/`PromptBuilderHint` bullets, add a fifth bullet:

```
  - SkillResource: change a skill resource's trust state or content
    ({skill, filename, op, content?, type?, description?, verifyHint?}).
    Triggered by clusters where server="skill-resource" — there, `tool` is the
    skill name and `errorClass` is the resource filename. `op` is one of:
      - "demote-provisional": no content change, just re-enters validation.
        Use this when you have no specific evidence of what is wrong.
      - "attach": replace the body with corrected content (required: `content`,
        same `type` as the original unless you have a specific reason to change
        it). Use this when the sample error messages point at a specific,
        fixable cause (e.g. a tool schema or fingerprint change named in the
        errors).
      - "delete": remove the resource entirely. Only propose this when a prior
        attempt line for this exact pattern already shows a ticket that reached
        "Escalated" — i.e. retraining was already tried and failed. Never
        propose delete as a first response to failure.
```

This is directive prose, not executable code — verify by reading, not by test, that the three `op` values and field names match `SkillResourceChange` in `src/RockBot.Host/SkillResourceApplier.cs` exactly (`Skill`, `Filename`, `Op`, `Content`, `Type`, `Description`, `VerifyHint`).

- [ ] **Step 7: Run this task's tests plus Task 1's**

Run: `dotnet test RockBot.slnx --filter "FullyQualifiedName~RepairTicketCreationPromptTests|FullyQualifiedName~ProvisionalValidationPassTests"`
Expected: PASS — all tests from both tasks.

- [ ] **Step 8: Commit**

```bash
git add src/RockBot.Host/DreamService.cs tests/RockBot.Host.Tests/RepairTicketCreationPromptTests.cs
git commit -m "feat: teach repair-ticket creation pass to propose SkillResource fixes"
```

---

## Completion

Run the full project test suite once both tasks are committed:

Run: `dotnet test RockBot.slnx`
Expected: PASS, no regressions, both new/modified test files green alongside the existing suite.

Then: final whole-branch review per `superpowers:executing-plans`'s Final Review section, against `MERGE_BASE = git merge-base main HEAD`.
