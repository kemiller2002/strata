---
id: SDE-DOCTRINE-008
title: Structural Locality and Bounded Reasoning Scope
status: accepted
version: 0.2.0
created: 2026-09-05
updated: 2026-09-05
related_documents:
  - architecture/FOUR-TIER-ARCHITECTURE.md
  - method/NAVIGATION-AND-CONTEXT.md
  - method/FEATURE-MANIFESTS.md
supersedes: []
superseded_by: []
tags: [doctrine, architecture, structural-locality, bounded-reasoning]
---

# Structural Locality and Bounded Reasoning Scope

Status: REQUIRED structural principles, RECOMMENDED review guidance, and
EXPERIMENTAL outcome claims as labeled below.

## Design objective

SDE seeks to bound the information humans and agents must simultaneously
infer to make a safe change. State and action constraints bound the legal
semantic space; structural locality bounds where the implementing
responsibilities live; navigation contracts make that context discoverable;
verification determines how the change is proven.

This **bounded reasoning scope** is an explicit SDE design objective. The
claim that it reduces cost or defects remains a candidate theory
[`TH-SDE-2026-0005`], not an established result.

## Separate four concepts (REQUIRED)

- **Semantic area** — a coherent feature, workflow, or concern whose behavior
  belongs together.
- **Semantic authority** — the singular representation that decides a
  semantic fact or rule.
- **Responsibility cluster** — an independently navigable group within a
  semantic area, such as types, rules, transitions, capabilities,
  projections, effects, or diagnostics.
- **Physical module/file** — a source container chosen for implementation and
  human-scale navigation.

The useful hierarchy is:

```text
System
  → Semantic Area
    → Semantic Authority
      → Responsibility Cluster
        → Physical Module / File
```

A semantic area may legitimately span multiple files. Semantic locality
MUST NOT be interpreted as “one semantic area equals one physical file.”
Conversely, splitting an authority across files MUST NOT create independent
copies of the rule it owns.

> Physical decomposition organizes responsibility; it does not multiply
> semantic authority.

## Bounded feature ownership (REQUIRED)

For a meaningful bounded feature, ownership should make the following
reasonably discoverable as applicable:

- domain and presentation state;
- commands, messages, events, and legal transitions;
- invariants, guards, evidence, capabilities, and obligations;
- effect declarations and important effect contracts;
- inbound and outbound interfaces;
- local and boundary tests; and
- declared direct dependencies and composition points.

The complete set may span responsibility clusters and files. It should remain
a coherent, bounded semantic surface rather than an open-ended dependency
graph.

## Composition and cross-feature behavior (REQUIRED)

Top-level application components should primarily compose, route, and
coordinate bounded features. They should not accumulate every feature's
state, transitions, rendering, and interaction behavior.

Cross-feature behavior uses explicit contracts—commands, events,
capabilities, typed messages, published read models, effect requests, or
well-defined composition functions—rather than arbitrary access to another
feature's internal state. Dependencies should be explicit and directional;
circular semantic dependencies trigger architectural review.

Cross-boundary work is not forbidden. It follows the escalation and evidence
rules in [`method/NAVIGATION-AND-CONTEXT.md`](../method/NAVIGATION-AND-CONTEXT.md).

## Presentation state is state (REQUIRED)

Elmish, MVU, Redux, and similar architectures are not exempt from locality
rules. A substantial presentation feature should own its meaningful state,
messages/actions, transition logic, effects, view behavior, and tests; the
application shell composes it through explicit contracts.

The committed notes report an approximately 7,000-line Bolero/Elmish unit
concentrating `Model`, `Msg`, `update`, routing, rendering, and interaction
behavior. The architectural finding is **state and responsibility
concentration**, not that Elmish or state modeling is unsuitable
[`EV-SDE-2026-0006`]. The measured line count was not independently reproduced
in this repository and is not generalized as a universal limit.

## Human-scale physical decomposition (RECOMMENDED)

Avoid both tiny-file fragmentation and semantic mega-files. Review a source
unit when responsibilities can be understood independently, unrelated
transition families coexist, authoritative behavior is hard to locate, merge
contention is persistent, or normal changes repeatedly traverse unrelated
regions.

Physical line count is a review signal, not the rule itself. The provisional
defaults are:

| Physical lines | Default interpretation |
|---:|---|
| under 500 | normally no size concern |
| 500–999 | review emerging responsibility clusters |
| 1,000–2,000 | strongly consider decomposition |
| above 2,000 | explicit structural justification normally expected |
| above 4,000 | normally an SDE structural-conformance concern unless exceptionally justified |

Projects MAY configure different bands for language, generated-code, or local
conventions, with rationale. A threshold finding must never be represented as
proof of mixed responsibility or duplicate authority.

## Context Surface

The **Context Surface** of a change is the coherent semantic material required
to make and verify it safely:

```text
Context Surface(change) =
    affected state
  + affected transitions
  + relevant invariants
  + relevant contracts and interfaces
  + relevant tests
  + required direct dependencies
  + required composition context
```

This is a conceptual set, not a universal scalar. The architectural objective
is bounded, coherent context—not minimum raw LOC. Experimental proxies and
their limits are defined in
[`method/ENGINEERING-METRICS.md`](../reference/ENGINEERING-METRICS.md).

## Smells that trigger review

- one root state or action union grows with unrelated features;
- one update/reducer handles most application behavior;
- narrow changes routinely touch central application state;
- local tests require most of the application model;
- cross-feature writes or internal-type imports are common;
- `Helpers`, `Common`, or `Utils` becomes a weakly owned dumping ground;
- agents repeatedly read unrelated features or re-scan one large unit; or
- physical splitting causes the same legality rule to be reimplemented.

These are review prompts, not automatic proof of nonconformance.

## What this contract does not require

- a specific language, framework, runtime, or directory layout;
- one type/class per file;
- universal hard LOC limits or file-count targets;
- an arbitrary maximum Context Surface;
- eliminating all cross-cutting changes;
- code generation; or
- a custom SDE DSL.

## Evidence status

- **Established method findings used here:** semantic authority, explicit
  boundaries, earliest trustworthy detection, and behavioral verification for
  semantic no-ops [`EV-HN-2026-0002`, `0004`, `0005`].
- **Engineering guidance to validate:** responsibility clusters, feature-level
  structural ownership, composition-oriented roots, and the review bands.
- **Experimental:** causal reductions in context, tokens, time, cost, repair,
  or regressions [`HY-SDE-2026-0007`, `HY-SDE-2026-0008`].

