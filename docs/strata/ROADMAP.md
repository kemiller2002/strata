# Strata implementation roadmap

Derived from `REQUIREMENTS-ANALYSIS.md`. Defines the minimum coherent
architecture, the vertical slices, and the implementation order.

## Minimum coherent architecture

Strata adopts SDE's four-tier architecture (`.sde/architecture/FOUR-TIER-ARCHITECTURE.md`),
which is REQUIRED as SDE's canonical layering. Dependencies point downward only.

```
Tier 4  Host / External Effects
        Strata.Host.Postgres      Npgsql catalog introspection
        Strata.Host.PgParser      pgsqlparser adapter (native libpg_query)
        Strata.Cli                terminal + JSON output
            |
            v
Tier 3  Application / Orchestration
        Strata.Application        inspection use cases, retrieval, projections
            |
            v
Tier 2  Domain Execution
        Strata.Analysis           scope resolution, reference extraction,
                                  effect classification, graph construction
            |
            v
Tier 1  Semantic Model
        Strata.Semantic           schema objects, identity, evidence,
                                  certainty, resolution state, analysis scope
```

`Strata.Semantic` targets `netstandard2.0` and references only `FSharp.Core`,
so it structurally cannot reference Npgsql, protobuf or the parser. This is
mechanically checked (`scripts/check-semantic-architecture.sh`), following the
pattern SDE records as passing in every HelixNote trial.

### Where the notebook's sketch was changed

The notebook's Phase 4 sketch places "dialect parser + introspection adapters"
directly above the semantic model. Two adjustments, both driven by evidence:

1. **Scope resolution is a distinct responsibility in Tier 2**, between parsing
   and any dependency edge. `EV-STRATA-2026-B9C4` shows the parse tree is not a
   bound tree; without its own scope resolution Strata emits false edges.

2. **The parser AST never crosses into Tier 1 or 2.** Per D-005 and ER-014 the
   protobuf AST stays behind the Tier 4 adapter boundary; Tier 2 consumes a
   small Strata-owned extraction result. This keeps `Strata.Semantic` free of
   `Google.Protobuf` and keeps the AST replaceable when a SQL Server adapter
   arrives.

### Extension boundaries protected but not implemented

The architecture must stay compatible with later schema diff, reference-data
management, transition planning, deployment policy, deployment verification and
additional dialect adapters. Those are **not** implemented now. The boundaries
that protect them are: the dialect adapter interface (PR-001), the
resolution-state and evidence types in Tier 1 (PR-011, PR-014), and the
analysis-scope type (PR-021). Nothing else is built ahead of need.

## Vertical slices

| Slice | Name | Requirements | Delivers |
|---|---|---|---|
| S1 | PostgreSQL semantic inspection | PR-004..007, PR-017, PR-021 | inspect a live database; canonical model; deterministic serialization |
| S2 | SQL parsing and semantic extraction | PR-001..003, PR-008..011, PR-014, PR-015 | parse a corpus; resolve scope; extract reads/writes with explicit resolution states |
| S3 | Relationship and dependency graph | PR-012, PR-013, PR-016 | declared + observed edges with evidence; path and impact queries |
| S4 | SQL validation | PR-018, PR-019, PR-020 | validate candidate SQL against schema; first consequence-based policy findings |
| S5 | Agent retrieval experiment | HY-STRATA-2026-1E5D, -2A6F | measured comparison of raw corpus versus semantic retrieval |

Deployment (PR-022..024) begins only after S1–S4, per D-016 and §144.15.

## Implementation order

Ordered by dependency and by uncertainty reduction. The notebook's own
conclusion (§144) is that the next useful step is empirical, not more design.

| Priority | Work | Rationale |
|---|---|---|
| P0 | Baseline; four-tier skeleton; parser spike | done or in progress; parser risk is the largest |
| P0 | S1 semantic model + catalog introspection | every later slice consumes the catalog snapshot |
| P1 | Spike B remainder (live binding comparison) | decides how much binder logic Strata needs |
| P1 | S2 extraction with scope resolution | blocked on nothing; carries `RK-001`/`RK-002` |
| P2 | S3 graph + targeted retrieval | first user-visible leverage |
| P2 | S4 validation + first policy profile | needs `Q-007` answered |
| P3 | S5 agent leverage experiment | decides whether the agent thesis survives |
| P4 | Schema diff / drift | needs `Q-003`, `Q-004`, `Q-010`, `Q-020` |
| P5 | Limited safe deployment | needs `Q-008`, `Q-030` |
| P6 | Reference-data convergence | needs `Q-009`, `Q-025` |
| P7 | Explicit transition engine | last |

This follows the notebook's suggested P0–P7 pattern with one change: Spike B's
remainder is pulled to P1, ahead of graph work, because its outcome determines
whether dependency edges can be trusted at all.

## Traceability structure

```
notebook section
  -> PR-/ER-/NFR- item in REQUIREMENTS-ANALYSIS.md
    -> ROS record (DF-/HY-/EX-/EV-)
      -> work item (WI-nnnn)
        -> implementation (module path)
          -> verification (test path / check command)
```

`TRACEABILITY.md` holds the matrix. Rules:

- No requirement becomes implementation work solely because it appeared in
  prose; it must appear in the PR-/ER-/NFR- tables first.
- No implemented behaviour may lack a traced requirement, experiment, or
  justified engineering support task.
- Unresolved notebook items are recorded as open questions, not dropped.
