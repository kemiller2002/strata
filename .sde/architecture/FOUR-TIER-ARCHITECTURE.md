---
id: SDE-DOCTRINE-003
title: Four-Tier Architecture
status: accepted
version: 0.2.1
created: 2026-09-02
updated: 2026-09-20
related_documents:
  - architecture/BOUNDARY-PRESERVATION.md
  - architecture/STRUCTURAL-LOCALITY.md
  - architecture/DECISION-AND-EVIDENCE-SEMANTICS.md
supersedes: []
superseded_by: []
tags: [doctrine, four-tier, architecture]
---

# Four-Tier Architecture

Status: `supported` theory (see `TH-SDE-2026-0001`), REQUIRED as SDE's
canonical architectural layering.

## Naming note (read this first)

This is an **SDE-migration-era synthesis**: the exact "Semantic Model /
Transition / Orchestration / Host" tier vocabulary was assigned during the
State Programming → SDE migration to name a layering directly observable in
HelixNote's actual, enforced codebase structure — not copied verbatim from a
single HelixNote document. **It is not the same concept as HelixNote's own
"Four-Tier State-System Model"** (`HELIXNOTE-FOUR-TIER-CONFORMANCE-AUDIT.md`,
attributed there to an external "Echelon Foundry" brief), which classifies
kinds of state (Domain/Entity, Workflow/Process, Effect/External,
Presentation/Interaction) rather than architectural layers. Full
disambiguation and evidence: `research/evidence/EV-HN-2026-0001--*.md`. Do
not merge the two.

## The four tiers

### Tier 1 — Semantic Model

**Question:** What can be true?

Includes: domain identifiers, values, closed alternatives, state-specific
data, evidence, capabilities, obligations, uncertainty.

Must not know about: Postgres, HTTP, JSON, frameworks, browser APIs,
serialization, filesystems, infrastructure.

*HelixNote evidence:* `domain/HelixNote.Semantic` targets `netstandard2.0`
and references only `FSharp.Core`, so it structurally cannot reference
Npgsql, ASP.NET Core, or Giraffe. `scripts/check-semantic-architecture.sh`
verifies this mechanically, and passed on every architecture and condition
across all three experiments.

### Tier 2 — State Transition / Domain Execution

**Question:** What legal change may happen?

Includes: commands/intents, transitions, guards, invariants, evidence
requirements, capability requirements, outcomes, obligations, requested
effects.

Does not perform uncontrolled infrastructure effects — effects are
requested as data and executed by Tier 4.

### Tier 3 — Application / Projection / Orchestration

Coordinates use cases, projections, commands, requested effects, and
boundary translation. Must not become a second semantic authority — if
Tier 3 starts making its own decisions about what states are legal, the
Tier 1/2 boundary has failed.

*HelixNote evidence:* `api/` dispatches commands into the semantic core and
executes the effects it returns (per `HELIXNOTE-HANDLER-INTEGRATION-DESIGN.md`),
rather than re-deciding review semantics itself.

### Tier 4 — Host / External Effects

Owns database, network, HTTP, browser, filesystem, clock, queue, framework,
external services, and WASM/browser interop. Reports what actually
happened — including "we do not know what happened" as an explicit,
first-class outcome, not a swallowed exception.

*HelixNote evidence:* `domain/HelixNote.Semantic.Host.Postgres`,
`.Host.Wasm`, `.Host.Wasm.Browser`, `web-bolero/`, `web/`. Four hosts
(in-process, PostgreSQL, Giraffe HTTP, WASM/browser) were added across the
pilot's slices without changing a line of the Tier 1/2 core.

## Dependency direction

```
Host / Infrastructure
        |
        v
Application / Orchestration
        |
        v
Transition / Domain Execution
        |
        v
Semantic Model
```

Dependencies point downward only. Tier 1 never imports from Tier 2, 3, or 4.

## Ordo and ROS preservation guardrail

Ordo refines Tier 1/2 semantics; it does not alter the dependency direction.

No Ordo feature may require Tier 1/2 to perform external I/O or import browser, network, database, filesystem, provider SDK, persistence-adapter, or ROS implementation dependencies.

External effects follow this responsibility flow:

```text
Tier 2: decide / request effect
        |
        v
Tier 3: coordinate boundary
        |
        v
Tier 4: perform or observe effect
        |
        v
Tier 2: interpret Succeeded / Failed / Unknown
```

An Unknown external outcome may create a Tier 1/2 reconciliation obligation, but Tier 1/2 does not perform the probe.

ROS is not Tier 5. It observes execution history from outside the application stack. Application correctness and legal transitions must not depend on ROS being available.

See `DECISION-AND-EVIDENCE-SEMANTICS.md`.

## Layering does not imply one file per tier

The four tiers assign semantic responsibility and dependency direction. They
do not prescribe physical source-file size or require an entire tier or
semantic area to live in one file. Within a tier, meaningful features may be
decomposed into responsibility clusters while consuming the same singular
semantic authority.

The complementary structural hierarchy is:

```text
System → Semantic Area → Semantic Authority → Responsibility Cluster
       → Physical Module / File
```

Feature-level ownership should make state, transitions, invariants,
capabilities, effects, presentation state, interfaces, and tests locally
discoverable. Root application components primarily compose features;
cross-feature access uses explicit contracts. See
[`STRUCTURAL-LOCALITY.md`](STRUCTURAL-LOCALITY.md).

This also applies to presentation-state architectures. Elmish/MVU/Redux code
does not become structurally local merely because its state is explicit; a
root model/reducer may still concentrate unrelated responsibilities.

## What this doctrine does not claim

- It does not claim this four-way split is the only viable layering, or
  that it has been validated outside HelixNote/F#. See `TH-SDE-2026-0001`
  for confidence and limitations.
- It does not claim the tiers, on their own, prevent boundary failures when
  values cross between them — that is Boundary Preservation's job
  (`BOUNDARY-PRESERVATION.md`), not the tiering's.
- It does not claim a tier, semantic area, or semantic authority must equal one
  physical file, or that tiering alone bounds the context required for change.
