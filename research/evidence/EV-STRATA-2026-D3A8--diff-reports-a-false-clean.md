---
id: EV-STRATA-2026-D3A8
title: The diff reports "already matches desired state" while a foreign key, a check constraint and a column default all differ — a false clean, and 96% of real tables cannot be created
research_area: strata-deployment
evidence_type: test-result
source_title: Schema diff coverage probe against a live PostgreSQL database
source_author: claude
source_uri: strata_probe and strata_test on PostgreSQL 16.15
source_date: 2026-09-11
retrieved: 2026-09-11
created_by_agent: claude
confidence: high
supports: []
contradicts: []
related_theories: []
tags: [strata, diff, false-clean, coverage, deployment]
---

# Evidence Record

## Evidence summary

Two coverage gaps found by probing the shipped `plan` and `apply` commands
against a live database. The first is a correctness failure, not a missing
feature.

## 1. The diff reports a FALSE CLEAN

A table was declared in a project file differing from the database in three
ways: a foreign key removed, a check constraint removed, and a column default
changed from `'new'` to `'archived'`.

```
PLAN: 0 change(s), 0 suppressed  —  gate verdict REQUIRES-APPROVAL
  (no changes proposed)
Database already matches desired state; nothing to apply.
```

**Every one of the three differences is invisible, and Strata states the
opposite of the truth.**

`SchemaDiff.run` compares three things: tables present on one side only,
columns present on one side only, and — for columns on both sides — type and
nullability. Nothing else. Primary keys, foreign keys, unique constraints,
check constraints and column defaults are all loaded into the model, carried
through the snapshot on both sides, and then never compared.

This is the failure the whole project exists to prevent. `ER-008` keeps
"unknown", "unsupported" and "absent" apart precisely so that a clean result
means something; here a difference Strata did not look for is rendered
identically to a difference that does not exist. A `Suppression` would have
been honest — the mechanism already exists and is used for four other cases —
but constraint comparison does not reach it, because the comparison is not
attempted at all.

It is more dangerous than the refusal below, because a refusal is loud. A user
who runs `plan`, reads "already matches desired state", and believes their
database matches their files has been actively misled by the one tool whose
purpose is to tell them otherwise.

## 2. 96% of real tables cannot be created

Measured over the 49 user tables in `strata_test`:

| | |
|---|---|
| tables | 49 |
| **cannot be created by `apply`** | **47 (96%)** |
| have a secondary index | 4 |

`SchemaDiff.emit` refuses `CreateTable` when the desired table has any check
constraint or any column with a default, because the semantic model carries
that a default or check EXISTS but not its expression. The refusal is correct
given the model — creating a table without its checks would produce an object
differing from what was declared while reporting success — but it means the
`apply` path works on toy schemas and declines on essentially every real one.
`created_at timestamptz NOT NULL DEFAULT now()` is enough to trigger it.

## Root cause is shared

Both come from the same place: the model records the EXISTENCE of defaults and
check expressions, not their text. `Column.HasDefault` is a `bool`;
`CheckConstraint.Expression` is populated from the catalog for the introspected
side and left empty by the file loader.

## The fix that is probably right, and was not obvious

For **creation**, reconstructing DDL from the parse tree is the wrong approach.
The declaring file already contains exactly the DDL the project wants, written
by the author. Executing the file verbatim is both simpler and strictly more
faithful than deparsing a protobuf expression back into SQL — and it is what
`pgschema` does. Reconstruction can only lose information the file already has.

For **comparison**, the file's text cannot be compared against the catalog's
normalised rendering directly: PostgreSQL reports `DEFAULT 'new'::text`, not
`DEFAULT 'new'`, so a textual comparison would report a difference on every
column with a default. That is the same class of problem as the type-spelling
mismatch in `EV-STRATA-2026-A7C3`, and it needs the same treatment —
canonicalise to the catalog's rendering — or the honest fallback of reporting
the constraint as unclassified rather than claiming equality.

**Whatever is done, "not compared" must stop rendering as "no difference".**
That distinction is the fix; detecting the differences precisely is the
improvement on top of it.

## Reproduction

```
CREATE TABLE app.child (
  id bigint NOT NULL, parent_id bigint NOT NULL,
  status text NOT NULL DEFAULT 'new',
  CONSTRAINT child_pkey PRIMARY KEY (id),
  CONSTRAINT fk_child_parent FOREIGN KEY (parent_id) REFERENCES app.parent (id),
  CONSTRAINT ck_status CHECK (status <> ''));
```

Declare the same table without the foreign key, without the check, and with
`DEFAULT 'archived'`, then run `strata plan --allow-drops`.

## What this does NOT establish

- Not measured: how often real projects change a constraint versus a column.
  The 96% figure is about CREATION, not about how often the false clean fires.
- The 49-table sample is one synthetic database built for earlier experiments,
  not a survey of production schemas.
