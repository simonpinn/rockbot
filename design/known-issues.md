# Known Issues

Deferred findings and tech-debt items from code review that are real but out of
scope for the PR that surfaced them. Tracked here instead of GitHub Issues
because Issues are disabled on this fork (`simonpinn/rockbot`) — flip that on
and move these over if that changes.

Each entry names the finding, why it was deferred rather than fixed on the
spot, and what the real fix looks like when someone picks it up.

## From the `feature/instinct` stimulus/x-factor review (2026-11)

Source: full `/code-review` pass on the Stimulus Dispatcher + X-Factor Memory
implementation (`design/cognitive-tiers.md`). 8 other findings from the same
pass were fixed directly in that PR; these 7 were judged out of scope.

### 1. `SemaphoreSlim` disposed with no coordination against in-flight waiters
**File:** `src/RockBot.Host/FileXFactorMemoryStore.cs:87`

If the singleton is disposed during shutdown while a `StimulusDispatcher`
handler is mid-flight blocked in `RecordSightingAsync`'s `_fileLock.WaitAsync`,
that call throws `ObjectDisposedException` instead of completing.
Reachability depends on hosted-service-stop-before-singleton-dispose
ordering, which isn't established in this file.

**Why deferred:** same risk shape as #2/#7 below — inherited from the exact
pattern in `FileFailureClusterStore` this file was modeled on, not unique to
the PR that introduced it. A real fix is a codebase-wide pass across every
file store sharing this shape, not a one-off patch to a single file.

### 2. Debounce `Timer` race between file-watcher callback and `StopWatching`
**File:** `src/RockBot.Stimulus/StimulusRegistryLoader.cs:94`

The `_debounce` Timer field is read/disposed/replaced from both the
`FileSystemWatcher` callback thread and `StopWatching` with no shared lock. A
pattern file changing as the host stops can have `TriggerReload` dispose the
old timer, `StopWatching` dispose/null the same field, then `TriggerReload`
assign a new Timer that fires ~500ms later and calls `Reload()`, mutating
`StimulusPatternHolder` after the hosted service has already stopped.

**Why deferred:** `AgentProfileLoader.cs` (the file `StimulusRegistryLoader`
was deliberately modeled line-for-line on) has the identical unguarded
`_debounce` access pattern today, unmodified. Fixing only the newer copy
would leave the original carrying the same risk — the real fix touches both
files together.

### 3. Wavering evidence manually threaded through ~13 call sites instead of centralized
**File:** `src/RockBot.Host/XFactorEvidenceRecorder.cs:12`

Recording is manually threaded through ~13 separate handler call sites
instead of being centralized once inside `AgentLoopRunner.RunAsync`, even
though `AgentLoopRunner` already takes other store dependencies directly and
writes through them — in tension with the "pure compute loop that never
touches a store directly" rationale used to justify keeping this external. A
future (14th) handler has no compiler-enforced reminder to wire
`failureClusterStore` and call `RecordIfWaveringAsync`; it will silently
never record wavering evidence for that path.

**Why deferred:** this argues against a deliberate design choice (keep
`AgentLoopRunner` a pure compute loop with no store dependency), not a bug.
Legitimate trade-off worth a real conversation, not a fix folded into a
review pass.

### 4. No metrics counters for `RepairTarget.XFactorMemory`
**File:** `src/RockBot.Host/HostDiagnostics.cs:404`

Repair-ticket apply-pass outcome metrics special-case only
`RepairTarget.SkillResource`; the `RepairTarget.XFactorMemory` target has no
equivalent counters anywhere in `HostDiagnostics.cs` or `DreamService.cs`.
X-factor-memory down-weight repair tickets resolve or escalate with zero
metrics visibility, unlike `SkillResource` tickets, so dashboards/alerts
built on these counters can't detect problems in that repair path.

**Why deferred:** a conscious scope cut made during the original
implementation, not an oversight. Real gap for dashboard parity, additive
and non-urgent — not a correctness bug.

### 5. `StimulusRegistryLoader` duplicates `AgentProfileLoader`'s reload mechanism
**File:** `src/RockBot.Stimulus/StimulusRegistryLoader.cs:10`

Re-implements, near line-for-line (its own doc comment admits this), the
`FileSystemWatcher`+debounce+poll-fallback+directory-fingerprint reload
mechanism already in `src/RockBot.Host/AgentProfileLoader.cs`, with no shared
base class/helper extracted. A future fix to the watcher/poll-fallback logic
(e.g. the already-known Docker Desktop/Longhorn inotify workaround) must be
found and applied by hand in two files; missing one lets the two loaders'
reload behavior silently diverge.

**Why deferred:** deliberate — the design explicitly called for copying this
exact pattern. Real DRY cleanup, but a bigger, separate refactor (extract a
shared base), not scoped to the PR that introduced the second copy.

### 6. 4+ files duplicate the JSONL snapshot persistence pattern
**File:** `src/RockBot.Host/FileXFactorMemoryStore.cs:369`

Re-implements the hot-dict+append-only-JSONL+periodic-snapshot persistence
shape (and its own `ResolvePath` helper) already used near-identically by
`FileFailureClusterStore.cs` and ~17 other files in `RockBot.Host`, with no
shared base class anywhere in the repo. A bug fixed in one of now 4+ copies
(`FileFailureClusterStore`, `FileSavedResponseStore`, `FileWipTracker`,
`FileXFactorMemoryStore`) won't propagate to the others, and any future
change to the pattern must be ported by hand to each.

**Why deferred:** matches this codebase's existing, already-accepted
convention — multiple pre-existing copies of this shape already coexist.
Worth a real shared-base extraction, but across all 4+ files at once, not
scoped to a single PR.

### 7. Blocking synchronous I/O inside async store methods
**File:** `src/RockBot.Host/FileXFactorMemoryStore.cs:197` (also 291, 295)

`LoadAsync` and `FlushAsync` use synchronous, blocking `FileStream`/`File.Move`
calls inside otherwise-async methods — the `FlushAsync` ones held under
`_fileLock` — violating this repo's "Async-first... no blocking calls"
convention, though the identical pattern already exists unchanged in the
pre-existing `FileFailureClusterStore.cs` this file is modeled on. On host
startup with a non-trivial JSONL file, `StartAsync` blocks on a synchronous
`FileStream`-open syscall before any await; on every `FlushInterval` tick,
concurrent `RecordSightingAsync`/`AdjustSalienceAsync` callers queue behind a
blocking `File.Move`+`FileStream(Truncate)` pair under the same lock,
stalling all x-factor writers on a slow/overlay filesystem.

**Why deferred:** inherited unchanged from the exact pre-existing pattern in
`FileFailureClusterStore.cs` this file is modeled on — pre-existing
codebase-wide behavior, not a regression introduced by the PR that surfaced
it.
