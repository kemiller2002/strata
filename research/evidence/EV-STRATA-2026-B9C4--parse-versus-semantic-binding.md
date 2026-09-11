---
id: EV-STRATA-2026-B9C4
title: Parser AST cannot resolve object identity without catalog and scope resolution (Spike B, first pass)
research_area: strata-semantic-binding
evidence_type: test-result
source_title: Strata Spike B — parsing versus semantic binding probe
source_author: claude
source_uri: scratchpad spike, cases reproduced in this record
source_date: 2026-09-11
retrieved: 2026-09-11
created_by_agent: claude
confidence: high
supports: []
contradicts: []
related_theories: []
tags: [strata, semantic-binding, resolution, spike-b, risk]
---

# Evidence Record

## Evidence summary

Walking the libpg_query protobuf AST yields object and column references, but
a meaningful fraction of them **cannot be resolved to database identity from
the AST alone**. One case is worse than unresolved: a CTE name is structurally
indistinguishable from a real table reference, so a naive extractor produces a
**false dependency edge on a real table that the statement never touches**.

This confirms notebook §144.1 ("parsing and semantic binding must be treated as
different systems... the largest hidden technical risk") with a concrete,
reproducible case, and it raises that risk from *incompleteness* to
*incorrectness*.

## Exact claim supported or contradicted

Supports P-008 (preserve uncertainty), P-019 (the database remains the
execution authority), D-018 (unknown/incomplete analysis remains explicit), and
§144.1.

Contradicts any design that treats "objects referenced" extraction as a
deterministic function of the parse tree. It is not. It is a function of the
parse tree **plus** lexical scope resolution **plus** a catalog snapshot
**plus** `search_path`.

## Relevant excerpt or data

Extraction over the AST, collecting `RangeVar` (relations), `Alias`, and
`ColumnRef` nodes:

```
qualified-join     SELECT o.id, c.name FROM sales.orders o JOIN sales.customer c ON c.id = o.customer_id
    relations : sales.orders, sales.customer
    aliases   : o=orders, c=customer
    columns   : o.id, c.name, c.id, o.customer_id          <- fully resolvable

UNQUALIFIED-cols   SELECT id, name, total FROM orders o JOIN customer c ON c.id = o.customer_id
    relations : orders[UNQUALIFIED], customer[UNQUALIFIED]
    columns   : id, name, total, c.id, o.customer_id       <- id/name/total unattributable

no-schema          SELECT * FROM orders WHERE id = 1
    relations : orders[UNQUALIFIED]                        <- needs search_path

star-expansion     SELECT * FROM sales.orders
    columns   : *                                          <- no column list

cte-shadowing      WITH orders AS (SELECT 1 AS id) SELECT id FROM orders
    relations : orders[UNQUALIFIED]                        <- FALSE EDGE

temp-table         CREATE TEMP TABLE tmp_x AS SELECT * FROM orders
    relations : orders[UNQUALIFIED], tmp_x[UNQUALIFIED]    <- temp not distinguished by a naive walk

func-overload      SELECT my_fn(1), my_fn('a') FROM t
    columns   : (none)                                     <- overload resolution needs catalog
```

## Interpretation

The cases divide into distinct resolution states that must not be collapsed:

| Case | Correct state | What would resolve it |
|---|---|---|
| Schema-qualified relation, alias-qualified column | `resolved` | AST alone |
| Unqualified relation name | `partially-resolved` | catalog + `search_path` |
| Unqualified column in a multi-relation query | `ambiguous` | catalog column lists |
| `SELECT *` | `partially-resolved` | catalog column lists at analysis time |
| CTE name shadowing a table name | `resolved` to the CTE, **not** the table | lexical scope resolution in Strata |
| `CREATE TEMP TABLE` target | `resolved` but **unmanaged** | AST persistence flag, then classification |
| Overloaded function call | `ambiguous` | catalog signatures + argument types |

Two consequences for Strata's architecture:

1. **Scope resolution is Strata's job, not the parser's.** libpg_query returns
   a parse tree, not a bound tree. CTE names, subquery aliases and table
   aliases establish lexical scopes that Strata must walk itself before any
   reference becomes a dependency edge. This is a required component of the
   first semantic slice, not a later refinement.
2. **`SELECT *` is a column-dependency hazard.** The notebook's flagship
   guardrail example — "drop column blocked because 8 readers found" — is
   unsound if `SELECT *` readers are not expanded against a catalog snapshot.
   A `SELECT *` reader of the dropped column is a real reader that naive
   column extraction reports as referencing no columns at all. Strata must
   either expand `*` against the catalog or mark the statement's column
   analysis explicitly incomplete; silently reporting zero column references is
   the dangerous failure.

## Limitations

- This is the first pass of Spike B. `search_path` behaviour, casts, operator
  resolution and ambiguous-column detection were **not** compared against a
  live PostgreSQL server; no server was available in this session. The live
  comparison half of Spike B remains outstanding.
- The extraction used here is a deliberately naive generic AST walk. It
  demonstrates what a naive extractor gets wrong; it is not Strata's intended
  extractor.
- Whether PostgreSQL's own `PREPARE`/`EXPLAIN` can substitute for implementing
  binder logic (Q-027) is untested and remains open.

## Counterevidence

Fully qualified SQL resolves cleanly from the AST alone. A corpus that is
uniformly schema- and alias-qualified would show far less ambiguity than these
cases suggest, so the practical severity depends on the corpus and should be
measured per repository rather than assumed.

## Reproduction or verification notes

Parse each statement with `pgsqlparser` 1.0.0, walk the protobuf message tree
generically, and collect `RangeVar`, `Alias` and `ColumnRef` nodes. The
`cte-shadowing` case is the load-bearing one: assert that a naive walk reports
a `RangeVar` named `orders` for a statement whose only `orders` is a CTE.
Verified 2026-09-11 on .NET SDK 8.0.131, linux-x64.
