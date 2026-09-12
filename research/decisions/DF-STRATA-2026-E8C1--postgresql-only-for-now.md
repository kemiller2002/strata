---
id: DF-STRATA-2026-E8C1
title: PostgreSQL is the only supported engine; the port boundary is kept honest now so other engines stay possible later, but nothing is built for them
status: accepted
decision_type: architecture
created: 2026-09-11
updated: 2026-09-11
created_by_agent: claude
confidence: high
supersedes: []
superseded_by: []
evidence: []
tags: [strata, dialect, ports, scope, phase-two]
---

# Decision Record

## Decision

**PostgreSQL only.** No other engine is supported, targeted, or designed for in
the current phase. No abstraction is built speculatively, no second adapter is
written, and no interface is generalised on the argument that another engine
might need it.

Other engines are **phase two**. What this decision buys them is not
abstraction, it is *discipline*: the boundary that already exists stays clean,
and the places where PostgreSQL has leaked past it are written down rather than
discovered later.

This is `ER-013` applied honestly — dialect differences are first-class and
there is to be no lossy generic abstraction. A premature `ISqlEngine` spanning
PostgreSQL and SQL Server would be exactly the lossy abstraction that rule
forbids, and would be built against one real engine and one imagined one.

## The boundary already exists and currently holds

`DialectPort` was moved from Tier 4 to Tier 2 during the first slice, so the
port is owned by the layer that depends on it rather than by the adapter that
implements it.

Verified 2026-09-11 — engine-specific package references by project:

| project | engine packages |
|---|---|
| `Strata.Semantic` | none |
| `Strata.Analysis` | none |
| `Strata.Application` | none |
| `Strata.Cli` | none |
| `Strata.Host.PgParser` | `pgsqlparser` |
| `Strata.Host.Postgres` | `Npgsql` |

No engine dependency exists above Tier 4. `scripts/check-semantic-architecture.sh`
enforces the direction mechanically.

## Known leaks, recorded rather than fixed

These are what a second engine would actually cost. None is being fixed now;
fixing them is phase-two work and doing it speculatively would be building for
an engine nobody has asked for.

1. **`CorpusPipeline` stamps `Dialect = "postgresql"` as a literal**
   (two sites). The pipeline is Tier 3 and should ask the injected parser what
   dialect it parsed rather than assert it. This is a small honesty defect
   today — swap the parser and the index would misreport what it indexed — and
   it is the first thing to fix when a second adapter appears.

2. **Completeness category names are defined by the PostgreSQL adapter and
   consumed above it.** `CatalogIntrospection` emits `relations`, `columns`,
   `constraints`, `indexes`, `view_definitions`, `routines`, and
   `Strata.Application.Validation` now branches on `relations` and `columns` to
   decide whether absence can be proven. That vocabulary is currently
   PostgreSQL's by accident rather than by contract. It was introduced
   knowingly on 2026-09-11 and is recorded here so it is not mistaken for a
   designed interface.

3. **`search_path` is a PostgreSQL concept named directly in Tier 1 and
   Tier 2.** Resolution semantics, `SchemaNotQualified`, and the scope chain
   all speak it. This is arguably correct under `ER-013` — a real engine
   concept modelled precisely beats a vague universal one — but a second engine
   will force the question of whether this is "PostgreSQL's search_path" or
   "the dialect's schema resolution order", and the answer is not currently
   written down.

## Rule going forward

While the project is PostgreSQL-only, a new engine-specific concept may be
introduced above Tier 4 — but it must be **recorded in this file** when it is.
The cost of phase two is the length of that list, and a list nobody maintains
is how a swappable boundary quietly stops being one.

## Explicitly NOT doing

- No second dialect adapter.
- No generalisation of `IDialectParser` beyond what PostgreSQL needs.
- No engine-neutral type system, DDL emitter, or capability-negotiation layer.
- No refactor of the three leaks above.
