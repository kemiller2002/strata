---
id: SDE-METHOD-001
title: State-Directed Engineering — Software Construction Method v0.2
status: draft
version: 0.2.0
created: 2026-09-05
updated: 2026-09-06
related_documents:
  - architecture/STRUCTURAL-LOCALITY.md
  - method/CHANGE-CLASSIFICATION.md
  - method/NAVIGATION-AND-CONTEXT.md
  - method/FEATURE-MANIFESTS.md
  - method/VERIFICATION-METHOD.md
  - method/AGENT-EXECUTION-RULES.md
supersedes: [method/CONSTRUCTION-METHOD-v0.1.md]
superseded_by: []
tags: [method, construction-method, sde-v0.2]
---

# State-Directed Engineering — Software Construction Method v0.2

**Status: Provisional / Engineering Validation.** V0.2 integrates structural
locality and deterministic context discovery into the v0.1 mechanical-first
method. It is a new experimental treatment and has not yet been validated as a
complete method on a non-HelixNote application. V0.1 remains frozen and
historically readable at `method/CONSTRUCTION-METHOD-v0.1.md`.

## Existing-system workflow

```text
UNDERSTAND AND CLASSIFY REQUEST
    ↓
IDENTIFY RESPONSIBLE SEMANTIC FEATURE
    ↓  (use repository semantic map when ownership is unclear)
READ FEATURE MANIFEST, OR THE MAP'S EXPLICIT NOT-NEEDED RATIONALE
    ↓
LOAD SMALLEST SUFFICIENT DECLARED LOCAL CONTEXT
    ↓  (state, transitions, invariants, capabilities, contracts, tests,
        required composition context)
RECORD EXPECTED MODIFICATION BOUNDARY
    ↓
IDENTIFY SEMANTIC AUTHORITY
    ↓
MAKE SMALLEST AUTHORITATIVE CHANGE
    ↓
COMPILE / TYPECHECK
    ↓
REPAIR COMPILER-DIRECTED OBLIGATIONS
    ↓
RUN AND REPAIR ARCHITECTURE CHECKS
    ↓
RUN AND REPAIR BOUNDARY / CONTRACT CHECKS
    ↓
RUN FOCUSED BEHAVIORAL TESTS
    ↓
FOLLOW DECLARED DEPENDENCIES WHEN MORE CONTEXT IS REQUIRED
    ↓
TARGETED DISCOVERY FOR A NAMED GAP
    ↓
REPOSITORY-WIDE SEARCH ONLY AS ESCALATION
    ↓
INTEGRATION VERIFICATION
    ↓
REPRESENTATIVE LIVE EFFECT / PERSISTENCE PROOF WHEN WARRANTED
    ↓
RECORD CONTEXT EXPANSION, DEFECTS, AND AVAILABLE TELEMETRY
    ↓
STOP WHEN ACCEPTANCE AND REQUIRED VERIFICATION ARE COMPLETE
```

The flow is a dependency order, not a claim that every change executes every
box exactly once. If declared local context is insufficient before the first
edit, use the context-escalation contract immediately. If compilation or a
later check exposes an undeclared obligation, return to the same contract and
record the expansion. A small/obvious area may use the repository map as its
complete routing declaration when `method/FEATURE-MANIFESTS.md` permits it; do
not create an empty manifest merely to satisfy the diagram.

## Why local-first and mechanical-first do not conflict

- **Local-first discovery** locates and understands the responsible semantic
  authority without reconstructing the whole repository.
- **Mechanical-first execution** lets the compiler and dedicated checks expose
  propagation obligations before broad exploratory search.

The method forbids neither reading nor search. It requires enough local reading
to avoid a blind semantic edit, then prefers declared dependencies and
mechanical feedback over speculative repository-wide exploration. See
[`method/NAVIGATION-AND-CONTEXT.md`](NAVIGATION-AND-CONTEXT.md).

## Failure-detection order

Use the earliest trustworthy mechanism capable of knowing the defect:

1. compiler/exhaustiveness for missing semantic cases;
2. architecture checks for illegal dependency direction;
3. boundary/agreement checks for missing or inconsistent representations;
4. focused and integration tests for present-but-wrong behavior;
5. runtime validation for weak/untyped re-entry; and
6. explicit uncertainty plus reconciliation for unknown external outcomes.

Do not optimize for compiler detection at any cost. A semantic no-op may pass
the first three layers and still require behavioral proof. The complete matrix
is in [`method/VERIFICATION-METHOD.md`](VERIFICATION-METHOD.md).

## Context expansion during work

When local context is insufficient, name the gap, follow declared dependencies,
then use targeted and finally repository-wide search. An undeclared dependency
or cross-boundary edit is architectural evidence, not routine inconvenience;
record the dependency, insufficient contract, boundary crossed, manifest
impact, and redesign question.

## Greenfield workflow

Greenfield work begins with the smallest semantic model sufficient for the
next meaningful vertical behavior:

```text
Requirements
  → identify domain concepts
  → identify important legal state
  → identify legal transitions
  → establish invariants and guards
  → create the semantic area, authority, and initial manifest
  → build the smallest meaningful vertical semantic slice
  → introduce only required host and persistence boundaries
  → verify end-to-end behavior
  → expand capability by capability
```

Do not use SDE as permission for unlimited up-front modeling. Add semantic
detail when it is required by the next vertical behavior or a known invariant.
Keep the repository map current as real features emerge; do not pre-create
empty feature bureaucracy.

## Change-class paths

See [`method/CHANGE-CLASSIFICATION.md`](CHANGE-CLASSIFICATION.md). A work item
may contain several site-level classes; classify each consequential site.

- **Presentation-only change:** local discovery → local change → focused
  interaction/visual/accessibility verification → stop. Meaningful
  presentation-state transitions are semantic changes, not automatically
  presentation-only.
- **Mechanical propagation:** mechanically directed repair → behavioral proof
  where the implementation body can be wrong → focused verification → stop.
- **Boundary change:** explicit boundary authority/representation decision →
  contract check → host/integration proof → stop.
- **Semantic change:** full workflow above.

## Construction optimization

Prefer one semantic decision to drive as many consequences as practical:
legality, validation, capability derivation, available UI actions, transition
execution, diagnostics, contract assertions, and tests. This reduces
independently maintained representations. It does not make code generation
mandatory and does not erase deliberate external representations.

## Stop conditions

A work item normally stops when:

- acceptance criteria are satisfied;
- relevant compiler/type checks pass;
- architecture checks pass;
- boundary/contract checks pass;
- required behavioral tests pass;
- required integration verification passes;
- representative live verification passes when warranted;
- no known blocking defect remains; and
- required evidence, context expansion, and available telemetry are recorded.

Skipped or unavailable checks are named with their implications. Do not keep
refactoring merely because further improvement is possible; capture genuinely
separate work in the repository's work system.

## Evidence status

- The mechanism-specific ordering through behavioral verification is grounded
  in `EV-HN-2026-0002` through `0005`.
- Local-first navigation, responsibility clusters, and feature manifests are
  v0.2 execution contracts with strong engineering rationale; their outcome
  benefits remain experimental (`HY-SDE-2026-0007`, `0008`).
- The complete v0.2 sequence is EXPERIMENTAL until independently trialed.
