---
id: DF-STRATA-2026-B1E7
title: Desired schema is authored as one declarative file per object, DACPAC-style, and Strata's job is to diff it against the live database
status: accepted
decision_type: product-architecture
created: 2026-09-11
updated: 2026-09-11
created_by_agent: claude
confidence: high
supersedes: []
superseded_by: []
evidence: []
tags: [strata, desired-state, schema-diff, dacpac, q-003]
---

# Decision Record

## Decision

**This answers `Q-003` — "Desired schema authoring source?" — which has blocked
P4 since planning.**

Strata is a **state-based** schema tool, not a migration-based one.

- The authoritative desired state is **one file per database object**. A table
  is stored as a file containing its full declarative definition, not as an
  accumulation of `ALTER TABLE` statements across dated migration scripts.
- The file layout follows the **Microsoft DACPAC / SSDT `.sqlproj`** model:
  objects organised as files in directories, the repository being the source of
  truth for what the schema *should* be.
- Strata's core operation is **compare and reconcile**: introspect the live
  database for actual state, read the object files for desired state, produce a
  **diff**, and from that diff produce an **update plan** that brings the
  database in line.

Migration scripts are, per notebook §85, *evidence and history* — they are not
the authority on desired state.

## Context

The notebook names this directly and repeatedly, and the requirements analysis
captured it, but the built system does not yet reflect it:

- §16 "DESIRED SCHEMA" — lists candidate authoring formats A–E and declines to
  pick one. This decision picks **D, database project artifact**, in the
  DACPAC shape.
- §18.1 "Convergent transitions": `actual state -> desired state`.
- §27 drift: "whether desired state changed or live DB changed".
- §85 "MAJOR ARCHITECTURAL CHALLENGE: SOURCE OF DESIRED STATE" — "If the
  desired-state source is ambiguous, Strata cannot safely deploy."
- `PR-022` "Compare desired schema against actual schema" and `PR-023` "Detect
  drift", both **deferred to P4**.

## Consequences

### What this makes correct that already exists

- `SchemaSnapshot` is a **state** model (`Table`/`View`/`Routine` with columns,
  keys, constraints, indexes, `ManagementScope`). It is already the right shape
  to hold BOTH sides of the diff, not just the introspected side.
- `ProposedChange.Change` is already a **diff vocabulary** — `DropColumn`,
  `DropTable`, `AlterColumnType`, `AddColumn`, `CreateTable`, `AddConstraint`,
  `TruncateTable`, `UnclassifiedChange`. It is what a diff should *produce*.
- `DeploymentGate` consumes a `Change list` plus the dependency graph and
  returns a verdict. That is the **safety check on a diff** and is reusable
  unchanged.
- The corpus dependency analysis answers "what reads this column" — which is
  precisely what makes the destructive half of a diff safe to apply.

### What is now revealed as missing

1. **No desired-state loader.** Nothing reads object files into a
   `SchemaSnapshot`. `FileCorpus` reads `.sql` as a *query corpus*, not as
   object definitions. Measured: given a `CREATE TABLE` with five columns, a
   primary key, a foreign key, a check constraint and two defaults, the parser
   adapter currently yields two relation mentions and **one** column — `total`,
   and only because it appears inside the CHECK expression as a `ColumnRef`.
   The `ColumnDef` nodes carrying name, type, nullability and default are never
   read. This gap is total, not partial.
2. **No diff engine.** `diff : desired -> actual -> Change list` does not exist.
3. **No plan generation.** `Change list -> ordered SQL` does not exist, and
   ordering is load-bearing (drop foreign keys before tables, create tables
   before the keys that reference them).

### What is now revealed as mis-shaped

`strata check <proposed.sql>` takes a **migration script** as input and
classifies its statements. That is the migration-based model this decision
rejects. The gate's *logic* is right; its *input* is wrong. It should take a
desired-state directory and diff against the live database.

This does not invalidate `EV-STRATA-2026-F8A2` — the gate's 9/9 determinism
result stands — but it reframes what was tested: the verdict engine, not the
whole gate.

## Constraints this decision inherits

- `NG-006` (non-goal): a schema diff must NOT assume absence means delete.
- §1437: "Do not delete unmanaged objects simply because they are not in
  desired state." `ManagementScope` already exists to carry this and must gate
  every drop the diff proposes.
- §86: rename vs drop-and-add is **not inferable** from two states. Needs
  explicit transition metadata. This keeps `Q-004` (stable managed-object IDs)
  open and now blocking.
- `P-010`: desired state alone does not authorize every transition.

## Still open

- Exact directory layout and file naming (`schema/<schema>/tables/<name>.sql`
  vs other). Not specified; needs a decision before the loader is written.
- How much of a `CREATE TABLE` the desired-state model must round-trip.
- `Q-004` — stable object identity, required for rename detection.
- Whether to interoperate with existing `.sqlproj` layouts.
