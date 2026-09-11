---
id: SDE-METHOD-007
title: Navigation and Context Discovery
status: draft
version: 0.2.0
created: 2026-09-05
updated: 2026-09-06
related_documents:
  - method/CONSTRUCTION-METHOD.md
  - method/FEATURE-MANIFESTS.md
  - method/AGENT-EXECUTION-RULES.md
  - architecture/STRUCTURAL-LOCALITY.md
tags: [method, navigation, context-discovery, escalation]
---

# Navigation and Context Discovery

Status: REQUIRED execution contract for SDE v0.2 work; its cost and
correctness benefits remain EXPERIMENTAL [`HY-SDE-2026-0008`].

## Preferred route

```text
Task
  → Repository semantic map (when ownership is not already unambiguous)
  → Semantic feature
  → Feature manifest when required, otherwise the map's not-needed rationale
  → Local state / transitions / invariants / contracts / tests
  → Declared dependencies as needed
  → Smallest authoritative modification
  → Verification
```

Repository-wide search remains available. It is an escalation/fallback, not
the default first move for a routine feature task with declared ownership.

## Local-first rule

> Begin with the declared semantic boundary responsible for the task. Expand
> outward only when that boundary does not contain enough information to make
> or verify the requested change.

Before the first change:

1. Classify the request and identify the likely semantic feature.
2. Read the repository semantic map if ownership is unclear.
3. Read the feature manifest, or the repository map's explicit reason that a
   separate manifest is not needed for this small/obvious area.
4. Load the declared local state, transitions, invariants, capabilities,
   interfaces/effect contracts, tests, and required composition context.
5. Record the expected modification boundary.
6. Decide whether the declared context is sufficient to identify the
   semantic authority and verification obligations.

This is not permission to edit blindly after opening a manifest. Load enough
declared context to understand the responsible authority. It is also not a
requirement to read every declared file if the task and manifest identify a
narrower sufficient subset.

## Context escalation contract

If local context is insufficient:

1. Name the missing information.
2. Follow declared direct dependencies first.
3. If still insufficient, perform a targeted symbol/path/search query for the
   named gap.
4. Use repository-wide search only when the previous routes cannot locate or
   verify the required context.

If an undeclared dependency is discovered, record:

- the dependency and how it was discovered;
- whether the manifest is incomplete;
- whether coupling leaks through feature internals;
- whether shared behavior is misplaced; and
- whether the semantic boundary should be revised.

If implementation requires a cross-boundary modification, record before or
with the edit:

- why the edit is required;
- the boundary crossed;
- which existing contract proved insufficient;
- whether the manifest needs correction; and
- whether redesign should be separate follow-up work.

Cross-boundary work is legal when justified. The contract converts unexpected
expansion into evidence rather than forbidding legitimate cross-cutting work.

## Relationship to mechanical-first execution

Local-first discovery answers **where the responsible authority is and what
declared context governs it**. Mechanical-first execution answers **how to
propagate and verify a change after that authority is understood**.

The combined rule is:

1. Acquire the smallest sufficient declared local context.
2. Change semantic authority first where appropriate.
3. Compile and follow compiler-directed obligations.
4. Run architecture and boundary/contract checks.
5. Run focused behavioral tests.
6. When a check exposes missing context, use the escalation contract.
7. Perform targeted manual discovery for known unguarded representations.
8. Use broad search only when the specific gap remains unresolved.

This avoids both broad speculative reading and uninformed authority edits.

## Declared and Actual Context Surface

The feature manifest predicts the **Declared Context Surface**. The material
actually required by execution and verification is the **Actual Context
Surface**. Categorize inspected/changed material as:

- declared feature boundary;
- declared dependency;
- undeclared dependency;
- unrelated; or
- cross-boundary edit.

Unexpected expansion is an architecture/navigation finding. A legitimately
cross-cutting change may expand substantially without being defective.

Where a study defines a reproducible counting unit, it may calculate:

```text
Context Expansion Ratio (CER) =
  Actual Context Surface / Declared Context Surface

Discovery Expansion (DE) =
  files read outside the declared feature boundary
```

CER near 1 is a useful directional hypothesis, not a universal target. Neither
CER nor DE is a pass/fail quality gate. Do not combine unlike units or invent a
denominator after execution; report the metric as missing when it was not
captured or cannot be reconstructed from durable evidence.

## Completion reporting

Record unexpected reads, undeclared dependencies, cross-boundary edits, and
manifest corrections in the work handoff. Ordinary expected reads need counts
or categories only when telemetry is available or the work is a serious
experiment.
