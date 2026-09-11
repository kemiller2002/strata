---
id: EV-STRATA-2026-E7A9
title: PostgreSQL catalog visibility does not imply accessibility, contradicting the notebook's permission assumption
research_area: strata-introspection
evidence_type: test-result
source_title: Strata catalog introspection integration test
source_author: claude
source_uri: PostgreSQL 16.15 local instance; tests/Strata.Tests/CatalogIntrospectionTests.fs
source_date: 2026-09-11
retrieved: 2026-09-11
created_by_agent: claude
confidence: high
supports: []
contradicts: []
related_theories: []
tags: [strata, introspection, permissions, risk, rk-004]
---

# Evidence Record

## Evidence summary

A role with **no `USAGE` privilege** on a schema still sees that schema's
tables, columns and constraints in `pg_catalog`. PostgreSQL's system catalog is
world-readable; revoking schema access hides the **data**, not the **metadata**.

This was discovered by an integration test that was written asserting the
notebook's assumption and **failed**.

## Exact claim supported or contradicted

**Contradicts notebook §135.1**, "Incomplete permissions make objects
invisible", as applied to catalog metadata. Requires the restatement of
`RK-004`.

## Relevant excerpt or data

Fixture:

```sql
CREATE SCHEMA hidden;
CREATE TABLE hidden.secret (id int PRIMARY KEY, v text);
REVOKE ALL ON SCHEMA hidden FROM strata;
```

Connected as `strata`, `CatalogIntrospection.introspect` returned
`hidden.secret` **with its full column list**, including types, nullability and
ordinal positions.

The test originally asserted `Assert.Empty hidden` — encoding the notebook's
assumption — and failed with the object present.

Confirming the two privileges differ:

| Probe | Result for role `strata` |
|---|---|
| `has_schema_privilege('hidden', 'USAGE')` | false |
| object present in `pg_class` | **true** |
| `SELECT * FROM hidden.secret` | `ERROR: permission denied for schema hidden` |

## Interpretation

There are three distinct states, and the notebook's model has only two:

| State | Metadata | Data | Strata can analyse structure? |
|---|---|---|---|
| Accessible | visible | readable | yes |
| **Visible but unreadable** | **visible** | **denied** | **structure yes, contents no** |
| Genuinely absent | not in catalog | n/a | no |

The middle row is the new one, and it is dangerous in both directions:

1. **Toward false confidence.** Strata can enumerate an object, describe its
   columns, and appear to have analysed it, while any operation against it
   would fail with permission denied. A deployment plan built on that structure
   is unexecutable by the role that produced it.

2. **Toward false absence.** The naive correction — filtering unreadable
   objects out of the snapshot — is worse. It makes the object look *absent*,
   and a schema diff that reads absence as intent to delete (`NG-006`) would
   propose dropping a table that exists and is simply not visible to this role.

3. **Privacy.** Strata reads metadata for schemas the connected role cannot
   use. Column names of an inaccessible table can be sensitive. This bears on
   `NFR-007` and notebook §33, and is not addressed by "least privilege" on the
   connection alone, because no privilege level short of a dedicated catalog
   restriction prevents it.

## Resolution applied

The relations query now reports `has_schema_privilege(...)` and
`has_table_privilege(..., 'SELECT')` alongside each relation. Unreadable
objects are **kept** in the snapshot — never dropped — and the completeness
block gains a `relation_access` category set to `Partial` naming every object
that is visible but unreadable. Because `Partial` is not `Complete`,
`Completeness.isFullyComplete` returns false, which in turn makes
`Scope.supportsAbsenceClaim` false: no absence claim can be built on a snapshot
containing unreadable objects.

Covered by three tests: the object is reported and not dropped; `relation_access`
is `Partial` naming `hidden.secret`; and the snapshot is not fully complete.

## Limitations

- PostgreSQL 16.15 only. Catalog visibility rules are stable across supported
  majors, but this was not verified on another version.
- Tested with schema-level `REVOKE`. Table-level revocation, column-level
  privileges, and row-level security were not tested and may behave differently.
- `has_table_privilege` was checked for `SELECT` only. A role that can `SELECT`
  but not `INSERT` is not yet distinguished, which will matter for deployment
  planning.
- The privacy consequence is recorded, not mitigated. Strata still reads the
  metadata.

## Counterevidence

None. The behaviour is consistent with PostgreSQL's documented catalog access
model; the notebook's assumption, not PostgreSQL, was wrong.

## Reproduction or verification notes

Create a schema, revoke all on it from a test role, connect as that role, and
introspect. Assert the object appears with columns AND that
`Completeness.stateOf "relation_access"` is `Partial`. Verified 2026-09-11
against PostgreSQL 16.15.
