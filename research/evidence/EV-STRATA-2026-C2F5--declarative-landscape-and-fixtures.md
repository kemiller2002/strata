---
id: EV-STRATA-2026-C2F5
title: pgschema already ships Strata's exact model for PostgreSQL; the dependency-aware-drop differentiator is genuinely unoccupied but most PostgreSQL codebases hide their queries in an ORM
research_area: strata-product-value
evidence_type: literature-review
source_title: Declarative schema tooling landscape and candidate fixtures
source_author: claude
source_uri: GitHub API and vendor documentation, observed 2026-09-11
source_date: 2026-09-11
retrieved: 2026-09-11
created_by_agent: claude
confidence: medium
supports: []
contradicts: []
related_theories: []
tags: [strata, competitive-landscape, desired-state, fixtures, orm-risk]
---

# Evidence Record

## Evidence summary

Taken after `DF-STRATA-2026-B1E7` fixed the product shape as declarative
one-file-per-object desired state diffed against a live database. Three
findings, in order of how much they should change the plan.

## 1. The position is already occupied for PostgreSQL

**`pgplex/pgschema`** — 1,034 stars, Go, Apache-2.0, created 2025-06-08,
pushed 2026-09-11. Sponsored by Bytebase, which has adopted it as the engine
for Bytebase's state-based PostgreSQL workflow. README states no current plans
to charge.

`pgschema dump --multi-file` emits `tables/`, `views/`, `functions/`,
`procedures/` with **one file per object** plus a main file of `\i` includes,
then runs a Terraform-shaped `dump -> edit -> plan -> apply` against a live
database. That is Strata's model, for PostgreSQL, shipping today, free.

Its gap is the whole of Strata's safety story: destructive changes get an
interactive `Do you want to apply these changes? (yes/no)` and
`--auto-approve`. **No hazard classification, no per-statement risk
categorisation, no dependency analysis.**

Others occupying nearby ground:

| tool | stars | desired state from | destructive handling |
|---|---|---|---|
| `ariga/atlas` | 8,722 | HCL, file, **directory**, ORM | 50+ analyzers; DS101/102/103 drop hazards, BC101/102 rename; policy-as-code |
| `stripe/pg-schema-diff` | 881 | **directory of DDL files** | named hazard taxonomy, each must be explicitly allowed |
| `sqldef/sqldef` | 3,158 | single file | drops disabled by default, `--enable-drop` required |
| `skeema/skeema` | 1,381 | **one file per object** (MySQL) | `--allow-unsafe` plus `safe-below-size` |
| `pgcodekeeper` | 145 | per-object project tree | real dependency graph; parses view/function bodies |
| `djrobstep/migra` | 3,049 | **two live DBs only** | `--unsafe` required; deprecated since 2022 |

## 2. The differentiator is real, and narrower than assumed

**No tool surveyed analyses application SQL in the repository to decide whether
an object is safe to drop.** Every tool's dependency reasoning stops at the
database boundary — `pg_depend`, parsed view and function bodies, or nothing.

But the claim needs three qualifications, each of which shrinks it:

1. **`sqlc verify` already does the analysis.** It takes committed application
   queries plus a proposed schema change and errors if any query would break.
   It is not a diff or deployment planner, does not introspect a live database,
   and requires sqlc's own query files — but the hard part is demonstrably done.
2. **DacFx/SSDT — the model Strata is copying — already does this.** A stored
   procedure referencing a dropped column produces build error SQL71501.
   Strata's delta over DacFx is PostgreSQL support plus queries that live
   *outside* the schema project, not the dependency check itself.
3. **Atlas has built the in-database half** — column-level lineage across
   tables and views, shipped v1.2.0 April 2026, marketed for exactly this
   ("an agent asked to drop a column can trace every downstream view").
   Cloud/paid, and in-database only, but the direction of travel is clear.

`stripe/pg-schema-diff` has a hazard named `HAS_UNTRACKABLE_DEPENDENCIES` — an
explicit admission that it cannot do what Strata proposes.

## 3. The ORM problem is the largest product risk

One-file-per-object declarative DDL is **common in SQL Server/SSDT and rare in
PostgreSQL**. GitHub code search returns ~6,100 `.sqlproj` files; the
equivalent PostgreSQL `schema/tables/*.sql` search returns ~830 hits, almost
all hobby-scale. No large, well-known PostgreSQL application uses this layout.

Worse for Strata specifically: in the PostgreSQL repositories that *do* use it,
application queries are in an ORM or in language string literals, so a
`.sql`-only analyser reads nothing and every verdict degrades to
`requires-approval` — honest and useless. This is the applicability risk
predicted before the survey, now with evidence behind it.

**The one shape where it works** is a database-centric codebase: logic in
functions, views and procedures stored as per-object files. That is what SSDT
projects are (stored procedures *are* the application SQL, and they are in the
project), and `nevs/pentabarf` is the PostgreSQL example — 114 table files plus
~150 PL/pgSQL function bodies. This is a real but narrow target market, and it
is the DACPAC-shaped world rather than the typical Django/Rails/Prisma one.

## Candidate fixtures

| repo | shape | app SQL present? | use |
|---|---|---|---|
| `Brightspace/bds-headless-client-example` | `schema/tables/*.sql` (5) + `schema/upserts/*.sql` (5) | **yes, explicit column names** | minimal PostgreSQL end-to-end fixture; cleanest available |
| `nevs/pentabarf` | `sql/tables/` (114), `views/`, `functions/**` | yes, ~150 PL/pgSQL bodies | large PostgreSQL per-object tree; old (~2013), mirrored |
| `microsoft/sql-server-samples` (wwi-ssdt) | `<Schema>/Tables|Views|Stored Procedures/*.sql` | yes, 140 routine files | scale + SSDT-semantics parity; 280 object files, ~33 tables |
| `NowinskiK/ssdt-training` | deliberate SSDT edge cases | n/a | negative-test corpus |
| `ucd-library/grain-variety-db` | `schema/tables/*.sql` (34) | **no** (R scripts) | schema-only fixture |
| `akrasnov87/us-db-ci_purgeable` | `SCHEMA/public/TABLE/<name>.sql` | no | pgcodekeeper's on-disk format |

PostgreSQL fixtures at scale can be manufactured by splitting any large public
schema with `pgschema dump --multi-file` or `PgSchemaExporter`.

## What this does NOT establish

- Star counts and dates are a single 2026-09-11 observation.
- `atlasgo.io` and `www.pgschema.com` were unreachable through the egress
  proxy; claims sourced to those two sites come from search-result summaries
  and the projects' GitHub repositories, not first-hand documentation.
- No tool was installed or run. Capability claims are from source, README and
  issue trackers, not from execution.
- The ORM prevalence claim rests on code-search counts and the sampled
  repositories, not a systematic survey.

## Decision-relevant consequences

1. **Do not invent a hazard vocabulary.** `DELETES_DATA`,
   `ACQUIRES_ACCESS_EXCLUSIVE_LOCK`, `HAS_UNTRACKABLE_DEPENDENCIES` are the
   emerging shared language; adopt `stripe/pg-schema-diff`'s taxonomy.
2. **pgschema, not Atlas, is the tool to beat** — same model, same workflow,
   permissive licence, corporate backing, 15 months old.
3. **The addressable market must be decided before the loader is built.**
   Either Strata targets database-centric PostgreSQL shops and sqlc/PgTyped
   users, or the analyser must read application source, not just `.sql`.
   That choice changes what gets built next.
