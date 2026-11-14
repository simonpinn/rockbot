# Reflex Tool Naming — Rename-Stable Identity

## Intent / why

A validated `SkillResource` (a non-provisional Wisp) is meant to be callable directly as a
"reflex" tool with zero LLM involvement — the whole point of `design/reflex-tools.md` and the
stimulus dispatcher built on top of it (`design/cognitive-tiers.md`). Tonight, investigating why
the Reflex Activity panel showed nothing, we found the one resource validated so far
(`capped-list-summary-wisp.json`, shape hash `da8b3366e7837c2c`) could never become a reflex tool
at all: its owning skill had been renamed by a dream consolidation pass to a long name, and
`reflex__{skill}__{filename}` came out to 85 characters — over the provider's 64-char tool-name
limit, so `ReflexToolCatalog` correctly refused to register it (it deliberately never truncates).

The deeper problem: the reflex tool's name is built from the skill's *current display name*,
which is explicitly mutable — dream passes rename skills as routine behavior. Any reflex tool's
name can change the moment its owning skill is renamed, silently breaking a stimulus pattern that
names it (`StimulusPattern.ReflexTool`), with nothing but a routine catalog warning to show for
it. The 64-char overflow was the symptom that made this visible tonight; the rename-fragility is
the actual defect.

## Goals

- A reflex tool's registered name never changes once assigned, regardless of how many times its
  owning skill gets renamed.
- A reflex tool's name is always well under the provider's 64-char limit, regardless of skill
  name or filename length.
- No change in behavior for how a reflex tool is *selected*, *fired*, or *reported* — only how its
  name is constructed.

## Non-goals

- Not addressing stimulus pattern authoring/candidates (that's point 1, a separate design pass).
- Not changing `DefinitionHash`'s computation or the promotion/validation pipeline.

## Constraints

- Every `SkillResource` the catalog considers is already required to be `Type == Wisp` and
  `Provisional == false` — and, as of tonight's earlier fix, every resource reaching that state
  carries a non-null `DefinitionHash` (SHA-256-hex16 — 16 lowercase hex chars) that survives
  renames. A resource still missing `DefinitionHash` (pre-existing legacy data, now none in
  practice since the manifest wipe) must be skipped with a warning, not crash.
- Nothing downstream parses the skill name back out of the tool name string — every consumer
  (`ReflexExecutor`, `AgentContextBuilder`, `ReflexToolSurface`) already resolves
  name → (skill, filename) through `IReflexRegistrationLookup.TryGet`. The LLM only ever sees the
  separate `Description` field. So dropping the skill name from the name string costs nothing
  functionally.
- Zero migration risk: the earlier manifest wipe means there are no live stimulus patterns or
  registered reflex tools anywhere today that reference the old naming scheme.

## Approach (one sensible way)

Anchor the reflex tool's name to `DefinitionHash` instead of to skill name + filename:

```
reflex__{DefinitionHash}      e.g. reflex__da8b3366e7837c2c
```

Always 24 characters. Rename-stable forever, by construction — it's keyed off the same
shape/content hash that's already proven (tonight) to survive skill renames and consolidation.

**Alternative considered and rejected:** keep a truncated, sanitized skill-name prefix ahead of
the hash for human legibility in raw logs (`reflex__{truncated-skill}-{hash}`). Rejected because
it reintroduces the exact same fragility under a different string the moment the skill is
renamed again — the whole point is a name that never has to change. The cosmetic win isn't worth
it, since no real consumer reads the raw name for meaning anyway (see Constraints).

## What's left to the builder

- `ReflexToolNames.Build` — change signature from `(skillName, filename)` to `(definitionHash)`;
  body becomes `$"{Prefix}{Separator}{Sanitize(definitionHash)}"`. Keep `Sanitize`/`IsAllowed` as
  a defensive no-op over an already-safe hex string.
- `ReflexToolCatalog.ReconcileOnceAsync` — build the candidate name from
  `resource.DefinitionHash`; skip + log a warning when it's null (mirrors the existing
  not-Wisp/provisional skip just above it). Keep the `FitsProviderLimit` check as a defensive
  belt-and-suspenders (now trivially always true, but still correct to check).
- `IReflexActivationHooks.TryGetToolName` — add a `definitionHash` parameter; its one caller
  (`AgentContextBuilder.cs:73`) already has the full `SkillResource` in scope (`r.DefinitionHash`)
  so this is a one-line change at the call site. `ReflexToolSurface`'s implementation still
  cross-checks the reverse lookup resolves to the expected `(skillName, filename)` pair —
  retained as-is, now defending against a genuine hash collision rather than a sanitization
  collision.
- Tests: `ReflexToolNamesTests.cs`, `ReflexToolCatalogTests.cs`, `ReflexToolSurfaceTests.cs` — the
  tests that build expected names via `ReflexToolNames.Build(skillName, filename)` switch to
  passing a `DefinitionHash` per test resource instead. The existing
  "`NameLongerThanProviderLimit`" scenarios (built today by using a 100-char skill name) no longer
  apply under the new scheme and should be replaced with a test confirming the built name is
  always short regardless of skill/filename length, plus the null-`DefinitionHash`-is-skipped
  case.
