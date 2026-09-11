---
id: SDE-METHOD-002
title: Change Classification
status: draft
version: 0.2.0
created: 2026-09-02
updated: 2026-09-05
related_documents:
  - method/CONSTRUCTION-METHOD.md
  - architecture/FOUR-TIER-ARCHITECTURE.md
tags: [method, change-classification]
---

# Change Classification

Status: EXPERIMENTAL. Classify each consequential change site into one of the
four classes below before or as it enters the Construction Method. A work item
may legitimately span classes; keep the sites and their verification
obligations distinct rather than assigning one ceremonious label to all work.

## Semantic Change

Changes what can be true, or what legal evolution means. Lives in Tier 1/2
of the Four-Tier Architecture.

Examples: state, transitions, invariants, evidence, capability, obligations,
effect semantics, uncertainty.

Goes through the **Full Path** of the Construction Method.

## Boundary Change

Changes representation or communication across a Tier boundary or across
the system's edge.

Examples: wire shape, API, route, persistence, serializer, parser,
browser/WASM contract, external protocol.

Goes through the Boundary Change path: explicit boundary decision, contract
verification, host/integration verification.

## Mechanical Propagation

No new semantic decision. A previously made decision must be propagated
because a compiler, architecture check, or other tooling exposed the
obligation (e.g., an `FS0025`-style exhaustiveness error after a Semantic
Change was already made elsewhere).

Goes through the Fast Path: mechanically directed repair, then focused
verification.

**Important, evidenced distinction:** a Mechanical Propagation site is not
automatically "free" or "safe." Experiment 1 [EV-HN-2026-0003] found that
even a compiler-forced site can be filled in with a value that compiles,
passes every existing check, and is still wrong — mechanical discovery of
*that a site needs an answer* is not the same as mechanical verification of
*whether the answer given is correct*. A behavior test remains warranted for
any Mechanical Propagation site whose content is not itself mechanically
checked (e.g., a fan-out arm's body, as opposed to its mere existence).

## Presentation Change

Display/interaction changes that do not change domain semantics.

Goes through the Fast Path: local change, then focused verification. Do not
force the full Semantic Change ceremony onto presentation-only work.

Presentation state is still state. If a change alters meaningful interaction
states, legal UI transitions, capabilities, effects, or invariants, classify
those sites as Semantic Changes even when they live in Elmish, MVU, Redux, or
another presentation architecture.

## Classifying a change that touches multiple tiers

A single requirement often produces work in more than one class (e.g., a new
`ObservedValue` case is a Semantic Change at Tier 1/2 *and* triggers
Mechanical Propagation at several Tier 2/4 sites *and* a Boundary Change at
the API/wire layer). This is normal and evidenced directly — Experiment 1
found 1 semantic decision produced 5 boundary decisions and 8 mechanical
propagations across 8 files [EV-HN-2026-0003]. Track each site's class
individually; do not classify the whole requirement as one thing.

## Defect classification is a separate axis

Change class describes the work being performed. Defect class describes what
failed. Record at least these defect classes when applicable:

| Defect class | Meaning |
|---|---|
| Domain / Product | externally observable business behavior is wrong |
| Semantic | state, transition, invariant, capability, obligation, or rule is wrong |
| Boundary | representation or translation across a boundary is wrong |
| Tooling / Build | compiler, dependency, build, command, or test runner failed |
| Repository / Automation | hook, workflow, packaging, or repository automation failed |
| Methodology | SDE guidance produced, permitted, or omitted a recurring problem |
| Experiment Harness | instrumentation or experiment infrastructure produced incomplete or incorrect evidence |

“Zero product defects” must not hide failures in another category. A single
incident may receive more than one defect classification when the mechanisms
are genuinely distinct.

## What this classification does not yet resolve

It does not yet specify a mechanical test for "is this Mechanical Propagation
or a disguised Semantic Change" beyond "did a new semantic decision get
made, however small." This is flagged as an open method question, not
silently resolved — see `research/CHRONOLOGY.md`'s open-questions carryover
and `WI-0019` (first engineering validation execution) for where this should be
tested against a real project.
