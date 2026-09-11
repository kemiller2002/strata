---
id: SDE-DOCTRINE-005
title: SDE Glossary
status: accepted
version: 0.2.0
created: 2026-09-02
updated: 2026-09-05
tags: [doctrine, glossary]
---

# SDE Glossary

Terms are grouped by where they originate. When a term is used differently
in different documents, that is called out explicitly rather than silently
picking one meaning.

## State Programming (paradigm) terms

- **Semantic state** — a named, explicit representation of "what can be
  true," as opposed to an inferred combination of flags/nulls.
- **Legal transition** — a change the model explicitly permits.
- **Invariant** — a property that must hold across every reachable state.
- **Evidence** (paradigm sense) — the basis for believing a state is true.
- **Capability** — what an actor or state is permitted to do next.
- **Obligation** — what must still happen for a transition to be complete.
- **Effect** — an externally observable consequence, requested as data.
- **Uncertainty** — a first-class explicit state for "outcome not yet
  known," distinct from `null`/exception/default.
- **Boundary** — a crossing point between Four-Tier responsibilities, or
  between the system and the outside world.

## Four-Tier Architecture terms

- **Tier 1 / Semantic Model**, **Tier 2 / State Transition–Domain
  Execution**, **Tier 3 / Application–Orchestration**, **Tier 4 /
  Host–External Effects** — see `doctrine/FOUR-TIER-ARCHITECTURE.md`.
- **HelixNote's "Four-Tier State-System Model"** — a *different*,
  same-named concept from an external "Echelon Foundry" brief that
  classifies state kinds (Domain/Entity, Workflow/Process, Effect/External,
  Presentation/Interaction), not architectural layers. See
  `research/evidence/EV-HN-2026-0001--*.md`. Do not conflate with the
  above.

## Boundary Preservation terms

- **Representation Collapse** — a closed/constrained value becomes
  weaker/open crossing a boundary.
- **Uncoordinated Duplication** — the same fact independently maintained
  in two+ places with no agreement mechanism.
- **Semantic Contract / Host Contract / Public Integration Contract** —
  the Three Contract Model; see `doctrine/BOUNDARY-PRESERVATION.md`.
- **Wire Contract** — the deliberately specified representation of a value
  crossing a boundary, as opposed to incidental serializer output.

## Engineering-metric terms (State Programming research)

- **RCS (Required Change Sites)** — the normalized count of distinct
  function/config-locations a semantic decision requires touching.
- **MDS / MaDS** — Mechanically / Manually Discovered Sites.
- **MDR / MaDR** — Mechanical / Manual Discovery Rate (MDS or MaDS ÷ RCS).
- **As-experienced MDR** — an agent's own, temporally-strict
  self-classification of which sites it found mechanically vs. manually.
- **Architecture-potential MDR** — what the architecture structurally
  guarantees, independent of which agent behavior happened to trigger it
  first. Introduced in Experiment 3 because raw self-reports were not
  directly comparable across two agents with different investigation
  strategies.
- **BCA (Boundary Change Amplification)** — boundary files (or mechanical
  boundary edits) per semantic decision. Measured at 4.0 (files) across
  three independent HelixNote experiments.
- **Search Reduction Rate / Repair Loop Reduction Rate** — relative change
  in search operations / repair loops between two conditions.
- **Semantic no-op** — code that compiles, satisfies architecture rules,
  and satisfies contract checks, yet performs the wrong or no meaningful
  behavior. Caught, in every HelixNote trial so far, only by integration
  testing or direct data inspection.
- **NOT OBSERVABLE** — the required label when a metric's telemetry
  genuinely does not exist for the environment that produced it. Never
  estimated; never silently omitted.

## Structural and navigation terms

- **Semantic area** — a coherent feature, workflow, or concern whose behavior
  belongs together; it may span multiple files.
- **Semantic authority** — the singular representation that decides a
  semantic fact or rule.
- **Responsibility cluster** — a human-scale implementation grouping within a
  semantic area/authority, such as transitions, validation, capabilities, or
  projections; not automatically a separate domain authority.
- **Structural locality** — organization that keeps a feature's owned state,
  behavior, contracts, and tests coherently bounded and composable.
- **Bounded reasoning scope** — the SDE design objective of limiting the
  information and inference required to make a safe change. Its causal effect
  on cost/correctness remains a candidate theory.
- **Repository semantic map** — a small top-level routing index from major
  semantic areas to locations and manifests.
- **Feature manifest** — navigation metadata declaring feature ownership,
  contracts, tests, dependencies, and modification boundaries by reference;
  it is not semantic authority.
- **Context Surface (CS)** — the state, transitions, invariants, contracts,
  tests, direct dependencies, and composition context required for a change.
- **Declared / Actual Context Surface** — the context predicted by a feature
  manifest versus the context execution and verification actually require.
- **Context Expansion Ratio (CER)** — Actual Context Surface divided by
  Declared Context Surface under a preregistered counting unit; an experimental
  diagnostic, never a conformance score.
- **Discovery Expansion (DE)** — files read outside a declared feature
  boundary, optionally classified as declared dependency, undeclared
  dependency, unrelated, or cross-boundary edit; an experimental diagnostic.

## SDE (methodology) terms

- **Change classes** — Semantic Change, Boundary Change, Mechanical
  Propagation, Presentation Change. See
  `method/CHANGE-CLASSIFICATION.md`.
- **Detection optimization** — making an already-necessary change visible
  earlier or mechanically.
- **Construction optimization** — reducing how many representations or
  implementation locations must exist at all. See
  `research/theories/TH-SDE-2026-0004--*.md` for why these must not be
  conflated.
- **Confidence classes** — REQUIRED, RECOMMENDED, EXPERIMENTAL, RESEARCH
  ONLY, DEPRECATED. See `doctrine/STATE-DIRECTED-ENGINEERING.md`.

## ROS (Repository Operating System) terms used alongside SDE

ROS defines its own identifier prefixes (`RP-`, `JR-`, `EV-`, `HY-`, `TH-`,
`EX-`, `DF-`, `CN-`, `GL-`) and lifecycle states
(`draft -> review -> accepted -> superseded | withdrawn` for research
artifacts). SDE reuses these exactly as ROS defines them in
`framework/REP-SPECIFICATION.md` and `framework/protocols/`; SDE does not
define a second identifier or lifecycle scheme.
