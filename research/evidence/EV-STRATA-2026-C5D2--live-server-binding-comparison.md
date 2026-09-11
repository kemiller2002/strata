---
id: EV-STRATA-2026-C5D2
title: Live PostgreSQL comparison confirms Strata's resolution model and demonstrates parser/server version divergence (Spike B, second pass)
research_area: strata-semantic-binding
evidence_type: test-result
source_title: Strata Spike B — live-server binding comparison
source_author: claude
source_uri: PostgreSQL 16.15 local instance, cases reproduced in this record
source_date: 2026-09-11
retrieved: 2026-09-11
created_by_agent: claude
confidence: high
supports: [HY-STRATA-2026-4D92]
contradicts: []
related_theories: []
tags: [strata, semantic-binding, spike-b, search-path, version-divergence]
---

# Evidence Record

## Evidence summary

The live-server half of Spike B, previously blocked for want of a database, is
now run against PostgreSQL 16.15. Three results:

1. **PostgreSQL agrees with Strata's resolution model** on every case tested,
   including the decisive CTE-shadowing case.
2. **Parser/server version divergence is real and reachable**, not theoretical:
   the pinned 17.5 parser accepts syntax the 16.15 server rejects outright.
3. Identifier folding behaves as `Identity.folded` models it.

## Exact claim supported or contradicted

Supports `DF-STRATA-2026-9B2E` — directly, against the real engine rather than
against Strata's own model of it. Supports `HY-STRATA-2026-4D92` for the
qualified/unqualified cases. Converts `RK-003` from an inferred risk into a
demonstrated one.

## Relevant excerpt or data

Server: `PostgreSQL 16.15 (Ubuntu 16.15-0ubuntu0.24.04.1)`.
Default `search_path`: `"$user", public`.

### CTE shadowing — the decisive case

```sql
SET search_path TO sales, public;
WITH orders AS (SELECT 1 AS id) SELECT id FROM orders;
-- returns: 1
```

`sales.orders` exists and is populated-capable, yet the query returns the CTE's
value. **PostgreSQL resolves the name to the CTE, not to the table.** This is
exactly what `ScopeResolution` implements and what a naive AST walk gets wrong.
`DF-STRATA-2026-9B2E` is now confirmed against the engine, not merely reasoned
from the parse tree.

### Ambiguous column

```sql
SELECT id FROM customer c1 JOIN customer c2 ON c1.id = c2.id;
-- ERROR:  column reference "id" is ambiguous
```

PostgreSQL refuses to choose. Strata returns
`Ambiguous (ColumnNotAttributable candidates)` for the same shape, so the two
agree that this is not resolvable. Qualifying the column resolves cleanly on
both sides.

### search_path

```sql
SET search_path TO sales, public;  SELECT to_regclass('orders');  -- orders
SET search_path TO public;         SELECT to_regclass('orders');  -- NULL
```

An unqualified name is resolvable only relative to an effective `search_path`,
and resolves to nothing when the schema is not on it. Strata's refusal to
default an unqualified name to `public` (`RK-006`) matches: defaulting would
have produced a confident reference to an object that is not visible.

### Identifier folding

```sql
SELECT 1 FROM public.MixedCase;
-- ERROR:  relation "public.mixedcase" does not exist
```

The unquoted reference folds to lower case and fails to find the quoted
`"MixedCase"` table. `Identifier.folded` models this, and the test
`quoted identifiers are not folded together with unquoted ones` asserts it.

### Parser/server version divergence — `RK-003` demonstrated

| Statement | Parser 17.5 | Server 16.15 |
|---|---|---|
| `SELECT * FROM JSON_TABLE('[]'::jsonb, '$[*]' COLUMNS (a int PATH '$.a'))` | **accepts** | `ERROR: syntax error at or near "COLUMNS"` |
| `MERGE INTO t USING s ON t.id=s.id WHEN MATCHED THEN UPDATE SET v=s.v RETURNING t.id` | **accepts** | rejected (`RETURNING` on MERGE is PostgreSQL 17+) |
| `SELECT 1` | accepts | accepts |

`JSON_TABLE` and `MERGE ... RETURNING` are PostgreSQL 17 features. Strata's
parser accepts both; the 16.15 server rejects them.

## Interpretation

The first two results are validation: Strata's scope resolution and its refusal
to guess are not merely defensible design choices, they reproduce PostgreSQL's
own behaviour on the cases tested.

The third is a **new, concrete defect risk in Strata's output contract**. A
statement can be reported by Strata as having parsed successfully, with
extracted references and classified effects, while being unexecutable against
the target server. Parse success has never implied execution safety (notebook
§12, "never equate parse success with execution safety"), but until now Strata
had no mechanism to detect the specific case where the *grammar itself*
diverges.

Consequence: `NFR-004` (report parser and dialect versions) is insufficient on
its own. Reporting both versions lets a human notice a mismatch; it does not
make Strata's own analysis state reflect it. A statement parsed by a newer
grammar than the target server should carry an explicit analysis state, in the
same family as the resolution states — not be silently reported as fine.

This is recorded as a gap, not fixed here: the fix belongs to the validation
slice (S4, PR-018) where the target server's version is known to the pipeline.

## Limitations

- One server major (16.15) against one parser major (17.5). The behaviour of
  other pairings is not measured.
- `to_regclass` was used to probe name resolution rather than a full binder
  comparison; this shows *whether* a name resolves, not every rule by which
  PostgreSQL would bind a complex query.
- Overloaded-function resolution was **not** meaningfully tested: the fixture
  defines only one `order_count` signature, so no overload ambiguity existed to
  observe. `Q-027` (whether `PREPARE`/`EXPLAIN` can substitute for binder
  logic) remains untested and open.
- Casts and operator resolution were not compared.
- The `"MixedCase"` grant case returned `permission denied` rather than the
  intended result, so quoted-identifier *retrieval* was verified only through
  the negative (unquoted) case.

## Counterevidence

None. No case tested showed PostgreSQL resolving a name in a way Strata's model
would have got wrong.

## Reproduction or verification notes

Create a PostgreSQL 16 database with a `sales` schema containing `customer` and
`orders`, and a `public."MixedCase"` table. Run each statement above. The
CTE-shadowing case is the load-bearing one: assert it returns the CTE's value
while `sales.orders` exists. For the version divergence, parse each statement
with `pgsqlparser` 1.0.0 and execute the same text against a 16.x server.
Verified 2026-09-11.
