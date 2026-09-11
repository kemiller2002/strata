---
id: DF-STRATA-2026-9B2E
title: Strata implements its own lexical scope resolution before emitting dependency edges
status: accepted
decision_type: architecture
created: 2026-09-11
updated: 2026-09-11
created_by_agent: claude
confidence: high
supersedes: []
superseded_by: []
evidence: [EV-STRATA-2026-B9C4]
tags: [strata, semantic-binding, scope-resolution, architecture, risk]
---

# Decision Record

## Decision

No reference extracted from a parse tree becomes a dependency edge until Strata
has resolved it against the statement's **lexical scope chain** (CTE names,
subquery aliases, table aliases) and, where the reference is still unqualified,
against a **catalog snapshot** and `search_path`. Every reference carries an
explicit resolution state: `resolved`, `partially-resolved`, `ambiguous`,
`unsupported`, or `unresolved`. These states are never collapsed.

## Context

The notebook treats the parse/bind gap as an analyzability limitation
(§144.1). Spike B shows it is also a **correctness** hazard.

## Evidence

`EV-STRATA-2026-B9C4`. The load-bearing case:

```
WITH orders AS (SELECT 1 AS id) SELECT id FROM orders
  naive AST walk -> RangeVar "orders"
```

A naive extractor attributes this to the real `orders` table. The statement
never touches it. That is a false edge, not a missing one.

Second case: `SELECT *` yields a single `*` column reference and no column
list, so a `SELECT *` reader of a column is reported as referencing no columns.

## Rationale

Strata's value proposition rests on claims like "8 readers found" (§130). A
system that emits false edges is worse than one that reports uncertainty:
it produces confident wrong answers about destructive operations, which is
precisely what P-006, P-008 and §144.2 forbid.

libpg_query returns a parse tree, not a bound tree. Nothing downstream can
recover the distinction if Strata discards it.

## Consequences

- Scope resolution is a **first-slice** requirement (PR-010), not a later
  refinement. S2 cannot ship without it.
- `SELECT *` must either expand against a catalog snapshot or mark the
  statement's column analysis explicitly incomplete. Silently reporting zero
  column references is prohibited (`RK-002`).
- The resolution-state type lives in Tier 1 and is part of the public output
  contract (`NFR-003`).
- Invariant for verification: *an unresolved or ambiguous reference must never
  appear as a declared dependency*.

## Alternatives considered

| Option | Outcome |
|---|---|
| Emit edges from the raw AST and accept noise | Rejected: produces false edges, not merely noisy ones. |
| Require all analyzed SQL to be fully qualified | Rejected: violates P-002 (SQL stays first-class) and is not true of real corpora. |
| Delegate binding to PostgreSQL via `PREPARE`/`EXPLAIN` | Not rejected — this is `HY-STRATA-2026-5E03`/`Q-027`, untested. It may reduce how much resolution Strata implements, but it requires a live server and cannot serve offline analysis. |
