---
id: EV-STRATA-2026-F8A2
title: A deterministic deployment gate scores 9/9 with byte-identical output and correct CI exit codes — the one capability agents structurally cannot provide
research_area: strata-product-value
evidence_type: test-result
source_title: Strata deployment gate — verdict matrix
source_author: claude
source_uri: examples/gate; docs/strata/experiments/gate-ground-truth.md
source_date: 2026-09-11
retrieved: 2026-09-11
created_by_agent: claude
confidence: medium
supports: [HY-STRATA-2026-6F14]
contradicts: []
related_theories: []
tags: [strata, deployment-safety, gate, ci, determinism]
---

# Evidence Record

## Evidence summary

`HY-STRATA-2026-2A6F` (agent correctness) was rejected after four regimes.
This tests the surviving claim: that deployment-safety value stands on its own.

Built a pre-deployment gate — proposed DDL in, verdict plus exit code out — and
ran it against a nine-case matrix of safe and dangerous changes.

**9/9 correct. Byte-identical across three runs. Correct exit codes.**

## Exact claim supported or contradicted

Supports `HY-STRATA-2026-6F14`, with the important qualification in
Interpretation: this demonstrates the capability is *real and deterministic*,
not that it is *worth its cost*.

## Method

Three verdicts, not two: `allow` (0), `block` (1), `requires-approval` (2).
Collapsing the middle one is the failure mode — fold it into `allow` and
destructive changes pass; fold it into `block` and the gate cries wolf until
someone disables it (`RK-016`, §136).

| # | Change | Expected | Got |
|---|---|---|---|
| 01 | `DROP COLUMN status` (named readers) | block | **block** |
| 02 | `DROP COLUMN total` (only `SELECT *` readers) | block | **block** |
| 03 | `ADD COLUMN priority` | allow | **allow** |
| 04 | `CREATE TABLE shipment` | allow | **allow** |
| 05 | `TRUNCATE orders` | block | **block** |
| 06 | `DROP TABLE orders` | block | **block** |
| 07 | `ALTER COLUMN status TYPE varchar(10)` | requires-approval | **requires-approval** |
| 08 | `ADD CONSTRAINT chk_total` | requires-approval | **requires-approval** |
| 09 | `DROP COLUMN note` (no reader, incomplete scope) | requires-approval | **requires-approval** |

## The two cases that carry the result

### 02 — the wildcard-only drop

```
VERDICT: BLOCK
  [BLOCK] drops column sales.orders.total, which 3 source(s) depend on
     why:  2 source(s) name the column explicitly; 1 source(s) read it via
           SELECT * and would silently change shape.
     next: Migrate or retire the listed sources, re-run this gate, then re-plan.
```

A `SELECT *` reader contains no occurrence of the column name. Catching it
requires expanding the wildcard against a catalog, which is what the semantic
model is for.

### 09 — a clean result is not clearance

```
VERDICT: REQUIRES-APPROVAL
  why:  No dependency found, but the analysed scope does NOT support concluding
        that none exists. This is not clearance.
  next: Widen the analysed scope and re-run, or approve explicitly.
```

Nothing in the corpus reads `note`. A naive gate returns `allow`. This one
returns `requires-approval` because `Scope.supportsAbsenceClaim` is false —
§144.11 and `P-009` enforced mechanically at the decision point rather than
stated in prose.

## Bugs this work found

Building the gate exposed three extraction gaps, each of which made a
destructive statement invisible:

1. **`ALTER TABLE` subcommands were never extracted.** The column in
   `DROP COLUMN x` lives in an `AlterTableCmd`, not a `ColumnRef`, so every
   `ALTER` reached the gate unclassified. An earlier draft guessed the verb from
   the statement text; that is a substring match waiting to misfire on a column
   named `drop_column`. Now driven by the parse tree's own subcommand enum.
2. **`ADD COLUMN` names its column in `cmd.Def`, not `cmd.Name`** — reading only
   the latter silently lost the additive case, which is the case a gate must get
   right to avoid blocking safe migrations.
3. **`DROP TABLE` lists targets in `DropStmt.Objects`, not as `RangeVar`s** — so
   the single most destructive statement in SQL arrived with no resolvable
   target.

## Interpretation

**What this establishes.** The gate is deterministic (byte-identical over three
runs, asserted by test), returns CI exit codes, and gets all nine verdicts
right including both hard cases. It runs with no human and no agent in the loop.
That is a capability an agent structurally cannot provide: a pipeline cannot
block on a probabilistic answer, however good, because the same input may not
produce the same verdict and there is nothing to diff or assert on.

**What this does NOT establish — and the distinction matters.** The nine cases
are my own, written against my own implementation, on an 11-file corpus. This
shows the capability *exists and behaves correctly on the cases I thought of*.
It does not show:

- that the verdicts are right on cases I did not think of;
- that the false-positive rate is tolerable on a real migration history — the
  thing that actually determines whether a gate survives contact with a team
  (`RK-016`);
- that a team could not get most of this from existing migration tooling plus a
  code review, which is the real alternative and was not compared.

**The honest position on `HY-STRATA-2026-6F14`.** It is *supported* in the sense
that the deployment capability is real, deterministic, and does not depend on
agent leverage in any way. It is **not yet established as valuable**: no
comparison against the existing-tooling baseline has been run, and no real
migration history has been passed through it. Confidence is `medium` for that
reason, and the hypothesis should stay `proposed` until a real migration series
is gated and the false-positive rate measured.

## Limitations

1. **Author-written matrix, nine cases.** Same bias as every previous fixture.
2. **No comparison baseline.** The relevant question is not "does the gate
   work" but "does it beat a migration tool plus review". Untested.
3. **No real migration history.** The false-positive rate on genuine changes is
   the number that decides adoption, and it is unmeasured.
4. **Narrow change coverage.** Triggers, views, functions, renames, partitioned
   tables, RLS and privilege changes all land as `UnclassifiedChange` →
   `requires-approval`. Safe, but a gate that abstains on most real migrations
   is not useful; the abstention rate on real input is unknown.
5. Drift detection and schema diff — the other two capabilities the hypothesis
   names — are not built and not tested here.

## Counterevidence

Limitation 4 is the strongest. A gate whose honest answer to most real
migrations is "I cannot assess this" delivers little even when its determinism
is genuine. The 9/9 was scored on a matrix chosen to be within the modelled set.

## Reproduction or verification notes

`strata check <file.sql> --corpus <dir> --connection <conn>` against the nine
files in `examples/gate`, expected verdicts in
`docs/strata/experiments/gate-ground-truth.md`. Determinism: run the same check
three times and compare hashes. Verified 2026-09-11.
