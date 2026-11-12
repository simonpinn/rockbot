# Cognitive Tiers: Stimulus and Instinct

> **Status:** implemented — Instinct remains a sketch with no plan of its own, but Stimulus and X-factor memory are built: `StimulusDispatcher`, `FileXFactorMemoryStore`, `XFactorEvidenceRecorder`, and the Blazor `Consciousness.razor` panel all exist in the codebase (`src/RockBot.Stimulus/`, `src/RockBot.Host/`). `reflex-tools.md`, `skill-asset-promotion.md`, and `self-repair.md` remain unchanged by this document.

## Problem

RockBot's self-improvement work so far gives the agent two of the three thought patterns a trained, experienced actor actually runs on. **Thought** — full LLM deliberation through `AgentLoopRunner.RunAsync` (`src/RockBot.Host/AgentLoopRunner.cs`) — has existed since the beginning. **Reflex** — a validated `SkillResource` registered as a directly-callable tool, at near-zero marginal cost per call (`design/reflex-tools.md`, Phases 1–3 shipped, Phase 4 in design) — is new this cycle.

But reflex, as built, isn't quite what the name promises. A biological reflex fires *instead of* deliberation: a stimulus hits a sensory receptor and the response runs without ever reaching the part of the nervous system that thinks. What's built is closer to trained procedural memory — "I know how to do X, and this turn I'm choosing to do it by name" — because every reflex call still requires an LLM, inside an `AgentLoopRunner` turn, to decide to call it. Nothing today lets an environmental pattern fire a reflex on its own. There is no stimulus.

There's also a third pattern missing entirely, not a variant of reflex: **instinct**. The motivating example (kitesurfing) is picking a kite size — a fast, confident choice made from the full gestalt of current conditions, built from having lived through many prior sessions' outcomes, not a fixed action replayed verbatim. A reflex has no decision content (the pattern matches or it doesn't, and the action is always the same). An instinct's whole value is the decision content — it's judgment, just judgment that doesn't have to be re-derived from first principles because enough prior experience has already been compressed into it.

Both gaps are related: a stimulus is what a reflex is currently missing as a *trigger*, and it's also the natural thing that would surface an instinct as a *decision point* rather than a fixed action. This document sketches both together, in relation to what's already shipped or in design.

## Goals

- Establish a shared model for the three tiers — thought, reflex, instinct — that's grounded in what RockBot's code actually does today, not an abstract analogy.
- Define stimulus as the trigger mechanism reflex currently lacks, built from infrastructure that already exists (the message bus) rather than a new signal type.
- Sketch instinct's shape clearly enough to distinguish it from both thought and reflex, and conservatively enough to inherit the project's existing "nothing is synthesized from scratch" stance (`skill-asset-promotion.md`).
- Name the real open questions a future design pass needs to answer, rather than quietly deciding them here.

## Non-goals

- A new ML/training pipeline. Instinct stays example-based and LLM-distilled — the same spirit as wisp capture, not a bespoke classifier or model.
- Any change to `AgentLoopRunner` being the sole LLM tool-calling entry point. A stimulus-triggered reflex fire involves no LLM at all for that invocation — it's a new *entry point into already-built reflex execution*, not an exception to that rule.
- Modifying `reflex-tools.md`, `skill-asset-promotion.md`, or `self-repair.md`. This is groundwork for later amendments to those documents, kept separate so each stays independently reviewable.
- A phased implementation plan. Unlike `reflex-tools.md`, this sketch doesn't commit to file-level changes yet — see Open Questions.

## The three tiers, grounded

| | Thought | Reflex | Instinct |
|---|---|---|---|
| Trigger | Any work needing judgment | Today: an LLM's in-loop decision to call it by name. Proposed: also a stimulus (below) | Proposed: a stimulus recognized as a decision point, or an explicit LLM request |
| Decision content | Full reasoning | None — the action is always the same once the pattern matches | The whole point — a context-sensitive choice among options |
| Cost | A full LLM tier call (or several, across a turn) | Near-zero marginal cost; no LLM involved in a stimulus-triggered fire | Cheap relative to thought, but not zero — some context interpretation is unavoidable |
| Trust mechanism | N/A — it's live reasoning, not a cached asset | Statistical validation (`Wisp`); human-reviewed PR merge (`Python` — see Parking Lot) | Built from aggregated reflex/wisp outcomes across varying contexts; reinforced or revised by its own recommendation outcomes |
| Existing code | `AgentLoopRunner.RunAsync` | `ReflexToolCatalog`, `ReflexExecutor`, `ReflexToolSurface` (`design/reflex-tools.md`) | None yet |

## A trust principle: review scales with absent judgment

One idea threads through the answers below: the review bar for a tier isn't about which language or mechanism it uses — it's about how much new *logic* gets to run with nothing, human or LLM, watching to catch a bad call.

- A **Wisp** reflex introduces no new logic — it replays a sequence of calls through executors that were already trusted individually. It earns trust statistically (repeated success across sessions), because the risk is bounded by what it replays, not by anything novel.
- **Python** reflex code and a **stimulus pattern** are both new logic that will execute unattended. Both get the same gate: a human-reviewed PR, same as any other change to what this system does — not two separate policies, one rule applied wherever thought is substituted by something else.
- **Instinct's curated examples** are the exception that proves the rule: they aren't logic, they're observed data (`skill-asset-promotion.md`'s own non-goal: nothing synthesized from scratch) feeding a still-LLM-mediated match. They earn trust the way a skill resource already does — statistically, through the existing repair loop — not through PR, because there's no new logic to review, only evidence to validate. If instinct's match ever became literal deterministic code rather than an LLM consult, that code would need a PR too, by the same rule.

This is why the Python-reflex decision (Parking Lot, below) generalizes cleanly to stimulus-pattern registration rather than needing its own separate justification.

## Stimulus: the missing trigger

A stimulus is a recognized pattern in system state that can fire a reflex, or surface an instinct as a decision point, without paying the cost of thought re-deriving "should I act, and how" from scratch.

RockBot is already a message-based swarm — `MessageEnvelope`, topic-based pub/sub with hierarchical dot-separated names, wildcard subscriptions (CLAUDE.md). The natural, lowest-new-infrastructure definition: **a stimulus is a registered pattern over this same bus** — a topic pattern plus a lightweight payload condition — not a bespoke new signal type. Detection is a subscriber shaped like any other bus consumer, watching for matches against patterns that validated reflex/instinct resources register.

Two things produce stimuli, and both land on the same bus, seen by the same dispatcher:

1. **External/environmental.** Inbound user messages, scheduled-task firings, A2A messages — any bus event that exists independent of, and prior to, any LLM call. A match here can fire a reflex with *no* `AgentLoopRunner` invocation at all for that turn. This is the pure biological case: action, no deliberation anywhere in the path.
2. **Raised by thought itself.** `AgentLoopRunner` does not publish to the bus directly (confirmed by reading `AgentLoopRunner.cs` — no `IMessagePublisher` reference) — but every handler that invokes it (`UserMessageHandler`, `ScheduledTaskHandler`, `SubagentRunner`, the A2A handlers) publishes the turn's results and completion back onto the bus as a normal part of how the swarm already works. That publication is itself stimulus-shaped traffic, with zero new plumbing required inside `AgentLoopRunner`. A reasoning turn's own conclusion becomes a trigger — for a later turn of the same agent, or for an entirely different agent/session — which is what makes a swarm-wide version of reflex/instinct fall out of the existing decoupled architecture rather than needing a new one.

What a stimulus triggers differs by tier:

- **→ Reflex:** the match carries no judgment — "does the pattern fire" is the entire question, and the action is fixed. Appropriate only for narrow, high-confidence patterns, the same bar wisp's exact/shape-hash matching already holds itself to.
- **→ Instinct:** the match says "this is a decision point," not "do X." It hands off to the instinct to pick among options given the context that came with it, rather than invoking one fixed action.

Registration is additive: a reflex or instinct resource that wants stimulus triggering carries its own pattern; everything built in Phases 1–3 registers none and keeps working exactly as it does today, explicit-call-only.

### Context representation: sparse channels, not a dense vector

A stimulus's context isn't a feature vector or an embedding — most real instincts fire on a small combination of signals, usually at most one from each of a few primary-receptor-like categories (sight, sound, smell, touch — not even all of them present at once), plus one extra, often-novel signal that's what actually tips it into firing. A dark night, otherwise silent, and just the flash of unknown eyes in the distance is enough context to elicit fear — three sparse slots, not a dense description of the whole scene.

Mapped onto RockBot: a **channel** is a category of signal a stimulus pattern cares about — which topic family fired, a payload's salient field, whether this participant/value has been seen in this slot before — and a pattern names at most one expected value per channel it cares about, never requiring every channel to be present. That sparsity is what keeps matching cheap (see Cost, below).

The **x-factor** slot is structurally different from the others: it's usually not "a known value in a known channel" but a *deviation from baseline* — the first unknown signal in an otherwise-unremarkable picture. Matching it deterministically needs a small recency/baseline memory per channel ("has this exact value been seen in this slot recently") rather than a static lookup table. That's the one piece of this sketch that needs its own state, not just a stateless rule — still deterministic and cheap, just not stateless.

### X-factor memory: persisted, surfaced, revisable

The baseline/recency memory above isn't scratch state — it's exactly the kind of thing worth keeping. A **persisted** record per `(channel, value)`: first-seen, last-seen, occurrence count, and a current salience weight, living in a durable store the same way skill resources and agent memory already do, not an in-process cache that resets on restart. The entire value of an x-factor memory is that it doesn't need re-learning session to session — the same shape as a validated reflex not needing to re-earn trust on every call.

It also needs to be **surfaced**, to two different audiences:
- **Human-facing**, through the same bus-fed visibility already sketched for reflex/stimulus firing (above) — a queryable or listable view, likely in the Blazor UI. A standing x-factor memory can become a blocker to success rather than a help, and a human is often the one positioned to notice that a particular aversion has outlived its usefulness.
- **Agent-facing** — thought-tier reasoning should be able to recall its own x-factor memories mid-deliberation. Thought already sits above the fast tiers in one direction (it can raise a stimulus, above); the same relationship should hold in reverse — it should be able to inspect, and question, whatever is currently gating a fast-tier response.

And it must **not be immutable**. Long-lived by default is the entire point — nothing here argues for decay-over-time or a TTL, which would just make these short-lived instead of durable. What has to exist instead is a deliberate, never-automatic way to revise one:

1. **Evidence-driven down-weighting.** `self-repair.md`'s founding problem is exactly this failure shape, just for a different kind of memory: "a misdiagnosis gets cached as fact and reinforces itself instead of being corrected." The operational signal: thought *questioning* an instinct's recommendation, or *indecisive*, wavering use of an instinct or reflex it would otherwise commit to cleanly — both are symptoms of a blocker, visible in the reasoning trace itself rather than in any external task-outcome metric. Detecting that is a textual-pattern problem `AgentLoopRunner` already solves elsewhere (`HallucinatedActionRegex`, `CapabilityDenialRegex`) — a new detector in the same family, not a new kind of signal. A cluster of this evidence against a specific x-factor triggers a repair-style correction: down-weighting first (cheap, reversible, the same "demote-provisional" instinct `self-repair.md` already applies elsewhere), escalating only if that alone doesn't change the outcome.
2. **Explicit human-requested reset** — the case your psilocybin analogy names directly: a deliberate, visible, human-confirmed action taken once a surfaced x-factor is recognized as a stuck blocker, parallel to `reflex-tools.md` Phase 4c's human-requested-trial override. Explicit, immediate, attributable — never the LLM quietly deciding on its own that an aversion no longer matters.

Both paths revise the memory's *current weight*, not its history: the record that something happened stays (it's evidence, the same bucket as an instinct's curated examples under the trust principle above — no new logic, so no PR gate), only how much it currently influences triggering changes. Erasing the history outright would remove the evidence a later pass might need to revisit the same question again.

### Who defines a stimulus pattern

Same governance as Python reflex code (see the trust principle above): a pattern definition is new logic deciding when thought gets bypassed entirely, so it goes through the same human-reviewed PR gate — never autonomous LLM self-registration. The channels a pattern can reference follow the same path — there's no separate taxonomy authority to stand up. A pattern's PR names whatever channel it needs; a small suggested vocabulary (topic family, a payload field, sender/participant identity, prior-seen-in-this-slot) is a starting point, not a closed or centrally governed list.

### Visibility

RockBot's Blazor chat UI (`RockBot.UserProxy.Blazor`) already has an activity-log mechanism for exactly this shape of thing: `ChatStateService`'s `ActivityLogEntry`/`MessageCategory` already surfaces `SubagentActivity` and `A2AActivity` as live bubbles during a session, fed purely from the bus — the UI has no direct reference to agent internals (`docs/blazor-ui.md`). A new consumer subscribing to reflex/stimulus-fired events could feed a new category the same way. The difference: subagent/A2A activity belongs to an active chat turn, while a stimulus-fired reflex — and an x-factor memory event — has no owning session at all. Resolved: a separate panel, not a per-turn bubble, streaming this live and continuously — a view onto what is effectively a background consciousness, running alongside any one conversation rather than inside it.

### Cost: deterministic code only, never an LLM per message

The dispatcher matching bus traffic against registered stimulus patterns must be plain deterministic code — presence/equality checks over the sparse channel slots above — never an LLM call per message. That's what keeps its cost bounded: the match is O(messages × registered patterns) with cheap per-check cost, the same shape every other topic-based bus subscriber in this system already has, not a new cost category.

## Instinct, sketched

**Modeled separately, not as a `SkillResourceType`.** Instinct doesn't execute the way `Wisp`/`Python` resources do — it's a decision aid, not an artifact with a body to replay. It needs its own store and lifecycle, not a new enum value bolted onto `SkillResource`.

Grounded in the kite-size example: picking among several options, fast, from the full current gestalt of conditions, where the right choice has been learned from many past instances rather than derived fresh each time. It's cheaper than thought, but it isn't zero-judgment the way a reflex replay is — it still interprets context, just against compressed prior experience instead of first-principles reasoning.

**Build shape, kept deliberately conservative.** `skill-asset-promotion.md` already states its own non-goal plainly: "nothing is synthesized from scratch; promotion captures observed working assets." Instinct should inherit that stance rather than become a statistical-model project. The MVP shape: a curated set of labeled `(context, choice, outcome)` examples — themselves observed, not synthesized — consulted by a minimal, cheap-tier LLM call that matches current conditions against the examples. Not a bespoke classifier, not a trained model.

**The data gap.** Today's execution records (`WispExecutionRecord`, and Python's post-fix equivalent) track succeeded/failed per resource — they don't capture the surrounding context under which a *choice* was made. Building toward instinct needs that context captured somewhere, but only for decision classes someone has actually flagged as instinct-candidates — a blanket, always-on context logger for every tool call would be a cost and privacy mismatch for something still unproven.

**Distillation.** A new dream pass, sibling to the existing `wisp-success-dream.md`/`wisp-failure-dream.md` and the `skill-resource-repair-dream.md` proposed in `reflex-tools.md`'s Phase 1, periodically turns accumulated examples into — or revises — the curated resource.

**Reinforcement and failure.** Reuse `self-repair.md`'s existing ticket/apply/verify shape rather than inventing a parallel one: a cluster of bad recommendations demotes, revises, or escalates exactly as a misbehaving reflex already does.

## Relationship to existing design docs

- **`reflex-tools.md`** — unchanged here. Stimulus-triggered reflex firing is additive to what Phases 1–3 already built; nothing about registration, execution, or the repair path changes for an explicit-call-only reflex.
- **`skill-asset-promotion.md`** — instinct's "nothing synthesized from scratch" conservatism is a direct inheritance of this document's own non-goal, not a new policy.
- **`self-repair.md`** — instinct's reinforcement/failure loop is proposed to reuse its ticket/apply/verify shape as-is, not a new one.

## Parking lot (unrelated decision, recorded so it isn't lost)

While scoping `reflex-tools.md`'s Phase 4, we settled that Python-typed reflexes should be promoted by human code review — a GitHub PR — rather than by a statistical execution-count bar or a chat-triggered one-shot trial. A merged Python resource stays in the dynamic skill store exactly as today (still fetched and run through `IScriptRunner` at runtime), just gated by `PromotedBy: pr-merge` instead of a counter; the execution-outcome signal fix (`design/reflex-tools.md` 4a) is still worth keeping, since it's what tells the dream pass a resource is PR-worthy in the first place. This doesn't belong in this document's scope — it's recorded here only so it carries forward to the next pass over `reflex-tools.md`'s Phase 4, which this document does not touch. It's also the first instance of the general trust principle above, not a one-off policy written for Python specifically.

## Open questions

None remaining at the sketch level. Every question raised during this discussion — minimum context representation, whether instinct needs its own resource type, pattern authorship, channel taxonomy, visibility, dispatcher cost, and x-factor persistence/revision and its blocker-evidence signal — is resolved in the sections above. This document is treated as converged; the next step is an implementation plan.
