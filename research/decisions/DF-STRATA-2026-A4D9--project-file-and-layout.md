---
id: DF-STRATA-2026-A4D9
title: A Strata project is a strata.json manifest plus per-object SQL files under schema/, and both validation and deployment read it
status: superseded
decision_type: product-architecture
created: 2026-09-11
updated: 2026-09-12
created_by_agent: claude
confidence: medium
supersedes: []
superseded_by: [DF-STRATA-2026-7D14, DF-STRATA-2026-C3A2]
evidence: [EV-STRATA-2026-C2F5]
tags: [strata, project-file, desired-state, layout, q-003]
---

# Decision Record

> **Superseded, in part.** `strata.json` and the `schema/<name>/<kind>/` layout
> stand. Two things here were replaced on 2026-09-12:
>
> - project membership is no longer a directory walk — an explicit `include`
>   list is required and every mismatch is a compile error
>   (`DF-STRATA-2026-7D14`);
> - `managedSchemas` is removed — the managed schema set is derived from the
>   directory names under `schemaRoot` (`DF-STRATA-2026-C3A2`).
>
> The `managedSchemas` rationale below is retained because it records *why* an
> explicit managed scope was required in the first place, which the replacement
> also satisfies.

## Decision

A Strata project is a directory containing **`strata.json`** and a tree of
per-object SQL files. `DF-STRATA-2026-B1E7` settled *that* desired state is
authored as object files; this settles *where they live and how they are
found*.

### `strata.json`

JSON, not TOML or YAML. Three reasons, in order:

1. Strata already owns a hand-written deterministic JSON writer (`Wire.fs`)
   built under Boundary Preservation. A second serialisation format would mean
   a second vocabulary to keep honest.
2. The repository already carries `ros.json`; a contributor meets one format.
3. `NFR-001` requires deterministic output, and `Wire.fs` already guarantees
   stable key order.

Shape (initial; fields are added as slices need them):

```json
{
  "strataVersion": "1",
  "schemaRoot": "schema",
  "managedSchemas": ["sales", "billing"],
  "corpusRoots": ["queries", "reports"]
}
```

- `schemaRoot` — where desired-state object files live.
- `managedSchemas` — **the explicit managed scope**. §1437 requires this to be
  explicit; anything outside it is `Unmanaged` and can never be dropped by a
  diff, no matter what desired state omits.
- `corpusRoots` — where application SQL lives, for the dependency analysis that
  makes destructive changes safe. Separate from `schemaRoot` because a table
  definition and a query that reads it are different kinds of input and must
  not be confused.

### Layout under `schemaRoot`

```
schema/
  <schema-name>/
    tables/<table>.sql
    views/<view>.sql
    functions/<function>.sql
    procedures/<procedure>.sql
```

Schema-first, then object type. This differs from `pgschema --multi-file`,
which puts object type first and handles one schema per invocation
(`--schema public`). Strata's requirement set is multi-schema from the start
(`RK-006`, `search_path` resolution, cross-schema seams), so schema must be a
directory level rather than a CLI flag.

One object per file. The file name is the object name. A file declares exactly
one top-level object; additional statements in it are an error, not a silent
merge, because "which file owns this object" must have one answer.

## Context

Driven by two capabilities requested together:

1. **Validate agent-written SQL against the schema** — `PR-018`, in scope
   since planning and never built.
2. **Dry-run the difference between project files and a live database, then
   execute it** — `PR-022`, `PR-023`, `PR-024`, all deferred to P4/P5.

Both need one artefact — a project that names its desired state — and the
validator needs it for the same reason the deployer does: an agent should be
able to check a statement against the schema *as the project declares it*, not
only against whatever happens to be deployed.

## Consequences

- The desired-state loader must extract far more from a `CREATE TABLE` than the
  adapter does today. Measured in `DF-STRATA-2026-B1E7`: five columns, a PK, an
  FK, a CHECK and two defaults currently yield **one** column mention. The
  `ColumnDef` nodes are never read.
- `managedSchemas` becomes the mechanical enforcement point for `NG-006` and
  §1437. It is not advisory.
- Validation gains a second source of truth (project files), so it must say
  which one it validated against. Validating against files and against the live
  database can disagree, and that disagreement **is drift** — it is a finding,
  not an error to hide.

## Alternatives rejected

- **TOML** (what `pgschema` uses). Rejected for the `Wire.fs` reason above.
- **Object type first, schema second** (`tables/sales.orders.sql`). Puts the
  schema in the filename, where it cannot be enumerated without parsing names.
- **Interoperating with `.sqlproj`.** Deferred. DACPAC is the conceptual model,
  not a compatibility target, and adopting MSBuild XML would import a large
  surface for no present benefit.

## Still open

- Whether `strata.json` also carries environment overlays (notebook §85 names
  "environment overlay" as part of the desired-state model).
- Reference data (`D-012`, notebook §3944) — `pgschema` handles this with a
  `data/` directory of CSVs. Not in this decision.
- `Q-004`, stable managed-object identity, still blocks rename detection.
