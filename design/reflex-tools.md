# Reflex Tools

## Problem

A validated `SkillResource` — a wisp a subagent converged on and that has since succeeded three times across distinct sessions (`design/skill-asset-promotion.md`) — is fully trusted, but it is still *data*. Calling it means fetching the skill, finding the resource, and explicitly dispatching it through `spawn_wisps`. No agent can call it the way it calls a real tool: by name, with a schema, without first knowing which skill it lives under.

The cost of that gap compounds with use. Every call to a validated wisp still pays for the fetch-and-dispatch dance, and — more importantly — every call to a skill that *hasn't yet* been promoted pays the full cost of an LLM re-deriving a tool-call sequence it has already proven correct dozens of times. The pattern is structurally identical to the asymmetry `skill-asset-promotion.md` already names: a working pattern exists, but nothing makes it cheap to reuse. That document solved "capture the pattern." This one solves "make the captured pattern directly callable."

A second, related gap: `skill-asset-promotion.md` Phase 5 treats a failing provisional resource as disposable — two consecutive failures and it is deleted. That is the wrong response once a resource is directly callable with no LLM in the loop to notice and compensate for a bad call. RockBot already has a closed-loop repair mechanism (`design/self-repair.md`) built for exactly this shape of problem — diagnose, apply a correction, verify, escalate only after repeated failure — and it already has an applier for skill resources specifically (`SkillResourceApplier`, `RepairTarget.SkillResource`). It is simply not wired to this failure path yet.

## Goals

- A validated, `Wisp`-typed skill resource is callable as a first-class tool — discoverable, schema'd, zero LLM cost to replay — without changing how wisps execute or are sandboxed.
- A resource that stops being callable (demoted or deleted) is unregistered and evicted from every session holding it, with no stale tool left in anyone's list.
- A resource that starts failing is retrained before it is deleted: a corrected or re-earned version gets a chance to prove itself again, through the repair mechanism that already exists for this.
- Deletion is the last resort — it happens only after retraining has itself failed repeatedly, not on the first or second bad call.
- Tool registration stays cheap: a growing number of validated resources does not blow out every agent's tool list, the way an all-eager MCP typed-tool surface already proved it would.

## Non-goals

- Changing wisp execution, sandboxing, or process isolation. Only the calling convention changes — direct named call instead of fetch-then-spawn. Replay runs through the same wisp runner, unchanged.
- Eliminating the fetch-then-spawn path. It stays as the fallback whenever a resource has no registered tool (not yet validated, or mid-retraining).
- Covering `Python`-typed resources in this phase. A wisp replays calls through already-trusted executors; a promoted script is sandboxed code whose exposure changes meaningfully once it is callable from anywhere, not just via a subagent's explicit decision to run it. Tracked as a fast-follow once this ships and is proven on wisps.
- Unifying with `McpWrapperCatalog`/`McpTypedToolSurface` internals. That machinery fingerprints and reconnects a live downstream server connection; a reflex has none. A structurally identical, independent sibling is cheaper to build and reason about than forcing a reflex to fit a connection-shaped abstraction it doesn't have.
- Retroactively reflex-ifying resources deleted under the old demotion rule before this ships.

## Architecture overview

```
Skill store (validated, Wisp-typed SkillResource)
        │ polled each reconciliation cycle (no change-notification exists)
        ▼
 ReflexToolCatalog ──builds──> ToolRegistration + ReflexExecutor
        │                              │
        │ unregister on demote/delete  │ replays via the existing wisp runner
        ▼                              ▼
 ReflexToolSurface (session LRU + namespace pinning)
        │ joins ChatOptions.Tools when activated
        ▼
   Agent's tool list — callable by name, zero LLM replay cost

 Failure path (replaces "2 fails → delete"):
   ReflexExecutor failure ──feeds──> FailureClusterStore (existing)
        │
        ▼
   RepairTicket(Target=SkillResource)  [self-repair.md, already built]
        │  new directive decides the op, evidence-based:
        ├─ demote-provisional  (cause unknown — re-earn trust, no content change)
        ├─ attach               (cause evident — corrected content, re-earn trust)
        └─ after MaxAttempts (default 3) failed repair attempts → Escalated
                │
                ▼
          delete (real demotion — now the last resort, not the first response)
```

This design is additive on top of two already-shipped systems: `skill-asset-promotion.md`'s provisional/validated trust gradient, and `self-repair.md`'s ticket/apply/verify loop. Nothing here changes either system's own contract; it redirects one failure path in the first, and adds one new consumer (a new directive) that feeds the second.

## Phase 1 — Redirect the failure path through repair

### Where

`design/skill-asset-promotion.md` Phase 5 (Amendment 1, in that file) and `src/RockBot.Host/DreamService.cs` — specifically `RunProvisionalValidationPassAsync` (the provisional sweep, ~line 5523), `BuiltInRepairTicketCreationDirective` and `BuildRepairTicketCreationPrompt` (the repair-ticket proposal pass, ~lines 7883 and 8015).

### Change

Two independent fixes against real, already-traced code — no new directive file, no new DreamService pass:

1. **`RunProvisionalValidationPassAsync`'s `ProvisionalAction.Remove` case stops deleting directly, and its failure-recording bug gets fixed in the same change.** Today, on `Remove`, it calls `_skillStore.RemoveResourceAsync` immediately, then records a cluster via `_failureClusterStore.RecordAsync(clusterKey, sessionId: null, ...)` — purely as an after-the-fact note; `sessionId` is always `null`. Since `GetEscalatableAsync`'s threshold requires `SessionIds.Count >= 2` *distinct* ids, and `FileFailureClusterStore` only adds a session id to that set when it's non-empty, a cluster recorded this way can never reach `SessionIds.Count >= 2` — redirecting this path without also fixing the recording would leave the resource stuck forever, neither promoted, repaired, nor removed. The fix: stop calling `RemoveResourceAsync` (except as a fallback — see below); record using the real `SessionId` values already present on the recent failing `WispExecutionRecord`s in scope at this point in the loop (not `null`), using each record's own `ErrorMessage`/`FailedToolName` as the sample text instead of a generic constant string, and only recording failures genuinely newer than the cluster's existing `LastSeen` (a still-failing resource is re-swept every dream cycle with the same historical records otherwise, inflating `Count` and refreshing `LastSeen` on dream cadence alone — not real evidence). Once failures genuinely span two distinct sessions, the cluster crosses `GetEscalatableAsync`'s threshold on its own and the repair-ticket creation pass below picks it up. When the repair loop itself is unavailable (disabled, or a collaborator is missing — `DreamService.RepairLoopEnabled` false), fall back to the old direct `RemoveResourceAsync` behavior rather than leave the resource in permanent limbo with nothing to ever revisit it.
2. **The repair-ticket creation pass learns about `SkillResource`.** The apply/verify/escalate loop (`RunRepairTicketApplyAsync`) is already fully generic over `RepairTarget` — unchanged. `SkillResourceApplier` needed one fix, caught in review: `demote-provisional` flipped `Provisional` true→true with no other effect when the resource was *already* provisional — which is the only state this failure path can ever produce, since `RunProvisionalValidationPassAsync` only sweeps provisional resources. That made the op a silent no-op: the ticket resolved on the first cheap verify with nothing actually changed, and the pattern was then permanently excluded from future proposals (a prior ticket must be `Escalated`, not `Resolved`, to try again) — stuck forever, worse than the old delete-on-two-fails. The fix: `demote-provisional` always restamps `CreatedAt` to now, resetting the validation window, regardless of whether `Provisional` was already true — a real retraining action either way. Separately, upstream of the applier: `BuiltInRepairTicketCreationDirective`'s prompt text (both its prose target list *and* its strict output-schema enum — both needed the addition) lists four target types and not `SkillResource`, so the LLM never proposes one for a `server="skill-resource"` cluster. A fifth block teaches it: such a cluster's `tool` is the skill name and `errorClass` is the resource filename; the `Change` shape is `{skill, filename, op, content?, type?, description?, verifyHint?}` matching `SkillResourceApplier.SkillResourceChange` exactly; `op` is evidence-based — `demote-provisional` when no specific cause is known, `attach` with corrected content when one is (e.g. a fingerprint-drift note in the sample error messages), and `delete` only when the cluster's own prior-attempt history shows a previous ticket for this exact pattern already reached `Escalated`. That last case requires `BuildRepairTicketCreationPrompt` to carry prior-ticket history per cluster, which it does not natively (it shows count/sessions/sample errors, nothing about tickets already tried) — extended to include, per cluster, a one-line note of any prior ticket's `Target`/op and final `Status` for that pattern. The directive also needs explicit verify-shape guidance for `SkillResource`: there is no tool call that proves a `demote-provisional`/`attach` fix worked yet (the resource has to actually be used again first, on its own schedule), so the guidance is to pick any cheap, reliably-succeeding call purely so the ticket resolves — and, critically for `delete`, one *unrelated* to the resource just removed, since a failing verify auto-reverts the apply and would silently undo the deletion.

### Acceptance

- A provisional resource that would have been removed under the old rule is not removed; the cluster it produces has real, non-null, non-empty session ids and real error text, drawn from the failing wisp records, and does not keep growing when the same old failures are re-swept with no new activity.
- A `server="skill-resource"` cluster that crosses the escalation threshold gets a `RepairTicket(Target=SkillResource)` proposed, with `demote-provisional` chosen absent specific evidence and `attach`/`delete` chosen when prior-attempt history or sample errors justify it; `demote-provisional` has a real, verifiable effect (a restamped `CreatedAt`) even on an already-provisional resource.
- A resource is deleted only once its ticket reaches `Escalated` — and that requires the creation pass to see, from `BuildRepairTicketCreationPrompt`'s prior-attempt note, that retraining was already tried for this exact pattern — and a `delete` ticket's own verify must not depend on the resource it just removed, or a failing verify will auto-revert the deletion.
- If the repair loop is unavailable, the old direct-removal behavior still applies — no configuration leaves a failing provisional resource permanently un-repaired and un-removed.
- This phase ships value with zero reflex tools registered anywhere — every existing skill resource gets a retraining chance before deletion, independent of Phase 2.

## Phase 2 — `ReflexToolCatalog` and `ReflexExecutor` (Wisp-only)

### Where

New project `src/RockBot.Tools.Reflex/`, sibling to `src/RockBot.Tools.Mcp/` (one new `<Project Path="...">` line in `RockBot.slnx`). Also touches `src/RockBot.Wisp/SpawnWispsExecutor.cs` (extraction — see below) and `src/RockBot.Tools.Mcp/McpServerNames.cs` (reserved name).

### Change

The original sketch of this phase assumed several things that turned out not to exist when checked against the actual code — corrected here rather than discovered mid-implementation, the same way Phase 1's review caught `SkillResourceApplier` needing a real change instead of none:

- **Reconciliation is a timer, not a watch.** `ISkillStore` has no change-notification of any kind — there is nothing to "watch." `ReflexToolCatalog` is its own `IHostedService` running a periodic reconciliation loop. Each cycle: list all skills, scan manifests for validated (`Provisional=false`), `Wisp`-typed resources, diff against what's currently registered, `Register`/`Unregister` the delta — structurally the same diff-and-swap `McpWrapperCatalog` already does for MCP tools, just timer-triggered instead of triggered by its `McpServersIndexed` message. `WispToolRegistrar`/`DreamCostToolRegistrar` are *not* the precedent here despite the original wording — each registers one static tool exactly once, forever, and never reconciles a changing set. `McpWrapperCatalog` is the only existing precedent for dynamic multi-tool registration in this codebase, and this phase mirrors it.
- **No schema exists yet — reflexes ship as zero-argument tools in this phase.** `SkillResource` carries no parameter schema, and a wisp's steps are literal `JsonElement`s fixed at authoring time with no external input point. "Schema from the wisp's declared parameters" didn't correspond to anything real. `ToolRegistration.ParametersSchema` is an empty-object schema here — a reflex in this phase is a named, zero-argument replay of a fixed pipeline. Accepting caller-supplied parameters would mean extending the wisp step format itself; that's out of scope, the same boundary as the existing "not changing wisp execution" non-goal, not something this phase assumes.
- **Eviction moves to Phase 3 — there's nothing to evict from yet.** `McpTypedToolSurface.Evict` (the hook the original sketch said to reuse) removes tool names from a *session's* active/pinned list — but that per-session activation list is `ReflexToolSurface`, which doesn't exist until Phase 3. This phase cannot evict from a structure that isn't built yet. Scoped down: on demote/delete, `ReflexToolCatalog` only unregisters via `IToolRegistry.Unregister`. A registered reflex is visible to every session immediately (the same unscoped visibility `WispToolRegistrar`'s/`DreamCostToolRegistrar`'s tools already have) until Phase 3 wraps registration with session-level activation/pinning/eviction, without changing this phase's registration contract.
- **Replay reuses the runner, but the runner's logging path is private — it needs extracting, not just calling.** The `(skillName, filename, succeeded)`-shaped event Phase 1 depends on is actually a `WispExecutionRecord` appended via `IWispExecutionLog.AppendAsync`; building one (canonicalize → hash → circuit-breaker check → `WispExecutor.ExecuteAsync` → log) happens entirely inside `SpawnWispsExecutor`'s private `ExecuteOneAsync`/`LogExecutionAsync`. This phase extracts that pipeline into a method both `SpawnWispsExecutor` and the new `ReflexExecutor` call — a real change to an existing file, not a "reused as-is" dependency.
- **No lease/ref-count machinery — a cheaper pattern already fits.** `McpServerConnections`' lease pattern exists because a live server connection is a genuinely shared, disposable resource; nothing in `ReflexExecutor` is. `McpWrapperCatalog` gets the same "an in-flight call is immune to a concurrent registry change" guarantee for free, just by reading one immutable record out of a `volatile`-swapped dictionary at the start of a call and using that local copy for the rest of it. `ReflexExecutor` does the same — no lease, no release, no disposal.
- **No `via=reflex` tag — the existing name tag already disambiguates for free.** `via=wrapper|invoke_tool` lives only on `McpDiagnostics`' MCP-specific counter — not the right place for a reflex call regardless. The generic, cross-backend counter (`ToolDiagnostics.Invocations`/`InvokeDuration`, tagged today by `rockbot.tool.name`/`rockbot.tool.status` at four call sites across `AgentLoopRunner.cs` and `RockBotFunctionInvokingChatClient.cs`) has no source/backend tag, and adding one would mean injecting `IToolRegistry` into both of those classes — two of the most heavily used and heavily tested components in the host — just to resolve a tag already implied by the tool's own name. Every reflex tool's name already starts with `reflex__`, so cost analysis (the feature's original motivation) is a `rockbot.tool.name` prefix filter away, at zero new risk. Not worth the blast radius for what the prefix already gives for free; revisit only if a real dashboard need shows the prefix filter is insufficient.
- **Reserved-name enforcement has a confirmed home.** `McpServerNames.Validate` already rejects server names containing the `__` separator; this phase adds one more check there rejecting the literal name `"reflex"`, closing the one real collision risk (an MCP server named exactly `reflex` producing tool names that share reflex's own prefix).

### Acceptance

- A validated Wisp resource is registered as a callable tool within one reconciliation cycle; its schema takes no arguments.
- A demoted-to-provisional or deleted resource is unregistered from `IToolRegistry` within one reconciliation cycle. (Session-level eviction is Phase 3's acceptance criterion, not this phase's — there is no session activation list yet.)
- A direct reflex call still appends a `WispExecutionRecord` via the same path `spawn_wisps` uses, so Phase 1's repair/validation read-path sees it exactly as if it had gone through fetch-then-spawn.
- A reflex call's invocation appears in the generic tool-invocation metrics tagged `rockbot.tool.name=reflex__...` like any other registered tool — already filterable by name prefix, no new tag needed.
- A reconciliation cycle racing an in-flight call does not fail the call in progress — it completes against the snapshot it started with.
- An MCP server cannot be registered with the literal name `reflex`.

## Phase 3 — `ReflexToolSurface` (lazy activation, namespace pinning)

### Where

New file(s) in `src/RockBot.Tools.Reflex/`, sibling to `McpTypedToolSurface`.

### Change

Per session, a bounded LRU activation list — proposed default 40, mirroring `MaxActivatedToolsPerSession` — where re-using a reflex keeps its place instead of resetting it. Independent of MCP's own activation budget: different kinds of surface, and sharing slots risks one starving the other.

Namespace pinning: touching one reflex under a skill namespace pins every validated reflex in that namespace into the session's list, bounded by a separate small cap — proposed default 3, mirroring `MaxPinnedServersPerSession`.

Discovery rides the existing skill-recall (BM25) search the LLM already uses, not a new search tool and not `mcp_find_tools` — reflexes are children of skills, not of MCP servers. When skill search surfaces a skill with validated reflex resources, they show as directly callable there.

### Acceptance

- Namespace pinning activates every sibling reflex in the touched namespace, bounded and LRU-evicted at the pin cap.
- Re-activating a reflex keeps its place in the session's list rather than resetting it.
- Skill search that surfaces a skill with validated reflexes shows them as directly callable.

## Phase 4 — Python-typed reflexes (fast-follow)

### Where

Extends `src/RockBot.Tools.Reflex/` once Phases 1–3 are proven on wisps.

### Change

Extend `ReflexToolCatalog`/`ReflexExecutor` to cover `Python`-typed resources. Open question to settle when this phase starts: should the trust bar before registering a Python reflex be higher than a wisp's (e.g. more successful validations), given it's sandboxed code rather than a replay of already-trusted tool calls?

### Acceptance

To be defined when this phase is scoped, after Phase 2's `via=reflex` metrics and Phase 1's repair-ticket data give a real baseline to set the bar against.

## Sequencing & dependencies

| Phase | Depends on | Risk | Rough effort |
|---|---|---|---|
| 1. Redirect failure path | None | Medium — new directive + DreamService wiring | Medium |
| 2. Catalog + executor | 1 (so a reflex that fails has somewhere to go other than deletion) | Low — reuses existing registry/executor patterns | Medium |
| 3. Tool surface | 2 | Low — mirrors a proven pattern | Medium |
| 4. Python fast-follow | 2, 3 | Medium — new trust-bar question | Medium |

Suggested order: **1 → 2 → 3 → 4.** Phase 1 alone improves every existing skill resource, reflex-exposed or not, and ships independently of the rest.

## Risks and open questions

- **Repair-pass misjudgment.** The new directive could pick `attach` with content that's still wrong, or `demote-provisional` repeatedly on a cause `attach` would have fixed faster. Mitigation: `RepairTicket`'s existing attempt/verify loop already bounds this — a wrong choice just consumes one of `MaxAttempts` before escalating.
- **Namespace pin granularity.** Skill namespaces vary in size; pinning a large one could fill the activation budget in one step. Mitigation: the activation budget (40) caps the total regardless of how a namespace pin fills it; revisit the namespace-pin-count default (3) empirically once real usage data exists, the way `worker-subagents.md` treats its own defaults.
- **Two independent LRU/pin systems to reason about** (MCP's and reflexes'). Mitigation: kept deliberately structurally identical so the mental model transfers directly; revisit merging only if usage shows no cross-pressure risk.
- **Reserved-word collision.** `"reflex"` as a reserved MCP-server-name prefix needs enforcement at server registration time, not just at reflex-tool naming time, or a future `mcp_register_server` call could create an ambiguous name.
- **Zero-argument-only reflexes.** Phase 2 ships reflexes that take no arguments (no schema exists today to do otherwise — see Phase 2's "Change"). A fixed, parameterless replay is still real value for the patterns that fit it, but it undersells what "callable tool" suggests to a reader expecting arguments. Parameterizing reflexes would mean extending the wisp step format itself — a genuinely separate, larger change, not assumed here or scheduled into Phase 4's scope without a fresh look.

## Migration

- No migration required for existing skills, memory, or wisps. `SkillResource`'s schema is unchanged; `Provisional`, `CreatedAt`, and the manifest shape already carry what Phase 2 needs to read.
- `RepairTarget.SkillResource` and `SkillResourceApplier` already exist and are unchanged by this work; only the trigger path into them changes (Phase 1).
- Resources already deleted under the old two-strikes rule are not retroactively restored.
