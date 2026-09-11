# Strata traceability matrix

Mechanical trace from source material to verification. Maintained per
`docs/strata/ROADMAP.md`.

```
notebook section -> requirement -> ROS record -> work item -> implementation -> verification
```

## Requirement to work item

| Requirement | Notebook source | ROS record | Work item | Slice |
|---|---|---|---|---|
| PR-001 parse PostgreSQL via real parser | §5, C-001 | `DF-STRATA-2026-4C7A`, `EV-STRATA-2026-7A31` | WI-0007 | S2 |
| PR-002 statement splitting | §8 | `EV-STRATA-2026-7A31` | WI-0007 | S2 |
| PR-003 PL/pgSQL parsing | §5.1 | `EV-STRATA-2026-7A31` | WI-0007 | S2 |
| PR-004 live catalog introspection | §6, C-002 | — | WI-0005 | S1 |
| PR-005 introspection completeness | §6 | — | WI-0005 | S1 |
| PR-006 canonical semantic model | §7, C-003 | `DF-STRATA-2026-D3F8` | WI-0004 | S1 |
| PR-007 deterministic serialization | §31 | — | WI-0006 | S1 |
| PR-008 corpus indexing with provenance | §8, C-004 | — | WI-0011 | S2 |
| PR-009 reference/read/write extraction | §8, §10, C-005 | `EV-STRATA-2026-B9C4` | WI-0009 | S2 |
| PR-010 lexical scope resolution | *(not in notebook)* `EV-STRATA-2026-B9C4` | `DF-STRATA-2026-9B2E` | WI-0008 | S2 |
| PR-011 explicit resolution states | P-008, §12 | `DF-STRATA-2026-9B2E` | WI-0004, WI-0009 | S1, S2 |
| PR-012 dependency graph | §9, C-006 | — | WI-0012 | S3 |
| PR-013 relationship graph | §9, C-007 | `HY-STRATA-2026-3C81` | WI-0012 | S3 |
| PR-014 provenance on derived facts | P-007, §9 | — | WI-0004, WI-0011 | S1, S2 |
| PR-015 effect classification | §10, C-008 | — | WI-0010 | S2 |
| PR-016 targeted retrieval | §14, C-010 | `HY-STRATA-2026-1E5D` | WI-0013 | S3 |
| PR-017 JSON + human output | §14, C-024 | — | WI-0006, WI-0013 | S1, S3 |
| PR-018 SQL validation vs schema | §12, C-011 | — | *not yet created* | S4 |
| PR-019 consequence-based policy | §11, C-009 | — | *not yet created* | S4 |
| PR-020 explain findings and next safe move | §130 | — | *not yet created* | S4 |
| PR-021 analysis-scope bounding | §129 | — | WI-0004, WI-0013 | S1, S3 |
| PR-022 desired vs actual diff | §17, C-012 | `EX-STRATA-2026-E5FB` | *not yet created* | P4 |
| PR-023 drift detection | §27, C-013 | — | *not yet created* | P4 |
| PR-024 deployment plan/execute/verify | §18/28, C-014/018/019 | — | *not yet created* | P5+ |

Work items are deliberately **not** created for S4 and beyond: `Q-007` and
`Q-021` block the policy profile, and `Q-003`, `Q-004`, `Q-010`, `Q-020`,
`Q-008`, `Q-030` block diff and deployment. Creating them now would encode
guesses as work.

## Engineering rules to enforcement

Rules are constraints, so they trace to a *mechanism*, not to a work item.

| Rule | Enforced by |
|---|---|
| ER-001 no own parser | `DF-STRATA-2026-4C7A`; dependency on `pgsqlparser` |
| ER-008 distinct uncertainty states | Tier 1 closed union + F# exhaustiveness (WI-0004) |
| ER-014 model smaller than AST | `Strata.Semantic` references only `FSharp.Core`; architecture check (WI-0003) |
| ER-017 retrieval not dumping | WI-0013 output contract; `NG-010` |
| ER-019 database is execution authority | resolution states; live-server deferral (WI-0014) |
| ER-020 inspectable decisions | provenance on every derived fact (WI-0004) |

## Hypotheses to experiments

| Hypothesis | Experiment | Status |
|---|---|---|
| `HY-STRATA-2026-1E5D` agent token reduction | `EX-STRATA-2026-D4EA` | proposed |
| `HY-STRATA-2026-2A6F` agent correctness | `EX-STRATA-2026-D4EA` | proposed |
| `HY-STRATA-2026-3C81` observed-join precision | `EX-STRATA-2026-C3D9` | proposed |
| `HY-STRATA-2026-4D92` offline resolution sufficiency | `EX-STRATA-2026-B2E8` | active, first pass done |
| `HY-STRATA-2026-5E03` PREPARE/EXPLAIN substitutes for binder | `EX-STRATA-2026-B2E8` | active, untested |
| `HY-STRATA-2026-6F14` value independence | deferred | proposed |

## Orphan checks

**Orphan requirements** (a requirement with no work item and no explicit
deferral): none. PR-018..PR-024 are explicitly deferred with named blocking
questions.

**Orphan work items** (a work item tracing to no requirement): none. WI-0003
traces to `DF-STRATA-2026-D3F8` as justified engineering support; WI-0014 traces
to `Q-005`/`Q-027` and two hypotheses.

**Circular dependencies:** none. The dependency order is
WI-0003 → WI-0004 → {WI-0005, WI-0006} → WI-0007 → WI-0008 → WI-0009 →
{WI-0010, WI-0011} → WI-0012 → WI-0013, with WI-0014 independent of all of them.

**Unresolved notebook items:** recorded in `context/RESEARCH-QUEUE.md` and
`REQUIREMENTS-ANALYSIS.md` §G (30 open questions), §H (11 deferred
possibilities) and §I (10 non-goals). None dropped.
