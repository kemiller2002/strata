---
id: EV-STRATA-2026-F4C6
title: Strata retrieval reduces agent context size 46-101x at 200 tables, but its scope block costs more than the answer
research_area: strata-agent-leverage
evidence_type: test-result
source_title: Strata Spike D — agent context cost harness
source_author: claude
source_uri: tools/spike-d-context-cost.py; PostgreSQL 16.15 local instance
source_date: 2026-09-11
retrieved: 2026-09-11
created_by_agent: claude
confidence: medium
supports: [HY-STRATA-2026-1E5D]
contradicts: []
related_theories: []
tags: [strata, agent-leverage, spike-d, context-cost, measurement]
---

# Evidence Record

## Evidence summary

Measured how much context an agent must consume to answer a database question,
with and without Strata.

At **4 tables** the reduction is **2.4x** — unimpressive.
At **200 tables** it is **46x–101x**.

The reason is the shape of the two curves: raw context grows with schema size,
Strata's answer does not. A second finding fell out of the same measurement and
matters more for the design: **Strata's scope-and-caveats block is larger than
the result it qualifies** for point queries — 1,703 bytes of scope against 485
bytes of answer.

## Exact claim supported or contradicted

Supports `HY-STRATA-2026-1E5D` (semantic retrieval reduces agent token
consumption) **for context size specifically**.

Does **not** test `HY-STRATA-2026-2A6F` (agent correctness). No agents were run.
That hypothesis remains `proposed` and untested; see Limitations.

## Method

| Condition | Content |
|---|---|
| **A — raw** | `pg_dump --schema-only` for the schema, plus every `.sql` file in the corpus. What an agent reads when it has no semantic layer. |
| **B — Strata** | The JSON answer from one targeted retrieval command. |

Both conditions restricted to the **same schema**, so the raw condition is not
inflated with objects Strata's role cannot read. Token counts are estimates at
4 characters per token; no tokenizer for the target model is available in this
environment. **Byte counts are exact and independent of that approximation.**

Harness: `tools/spike-d-context-cost.py`.

## Relevant excerpt or data

### Small fixture — `sales`, 4 tables

```
RAW: ddl 4,964B + corpus 1,930B = 6,895B   (~1,724 tokens per task)

  inspect sales.orders                  2,751B  ~688 tok
  relationships sales.customer          3,620B  ~905 tok
  path sales.orders sales.customer      3,051B  ~763 tok
  readers sales.orders                  2,651B  ~663 tok
  writers sales.orders                  2,549B  ~637 tok

  7 tasks: raw ~12,068 tok  vs  Strata ~5,074 tok   ratio 2.4x
```

### Large synthetic schema — `big`, 200 tables, 199 foreign keys

```
RAW (schema dump): 229,439 bytes  ~57,360 tokens

command                              bytes  ~tokens  ratio   result   scope+caveats
inspect big.entity_100               2,263      566   101x     485B         1,703B
relationships big.entity_100         2,542      636    90x     635B         1,826B
path big.entity_010 big.entity_020   5,031    1,258    46x   3,238B         1,703B
```

## Interpretation

**On the hypothesis.** The 2.4x figure at 4 tables and the 101x figure at 200
tables are the same mechanism seen at two points on a curve. Raw context is
O(schema size); a Strata point query is O(1) in schema size. The small-fixture
number is not a weak result — it is the result at a scale where the constant
overhead dominates. This is the first evidence that the agent-leverage thesis
has a real mechanism behind it rather than an assumption.

**On the overhead.** For `inspect`, 75% of the payload is scope and caveats.
That is the price of the honesty design — every answer carries what was and was
not analysed — and it is why the small-fixture ratio was unimpressive. Two
consequences:

1. The overhead is **constant per answer**, so it is amortised away as schema
   size grows. It does not threaten the thesis at realistic scale.
2. It is nonetheless **repeated verbatim** across answers in a session. The
   scope block is identical for every query against one snapshot. Emitting it
   once per session, with answers referencing it, would cut a multi-query
   agent session's Strata cost by roughly two thirds without removing any
   information. This is a concrete optimisation, recorded but not implemented.

**On path queries.** `path` is the outlier at 46x because its result grows with
path length, not schema size — a 10-hop path carried 3,238 bytes of edges, each
with full provenance. Path answers are O(path length), not O(1). An agent
asking for a long path in a large schema still wins, but by less.

## Limitations

These are substantial and the headline number should not be quoted without
them.

1. **Agent correctness was not measured.** `HY-STRATA-2026-2A6F` — fewer wrong
   joins, fewer hallucinated columns, fewer repair loops — requires running
   agents under controlled conditions with a graded task set. None of that
   happened here. A context-size reduction is **not** evidence of a correctness
   improvement, and notebook §134 is explicit that token reduction alone does
   not establish business value.
2. **The 200-table schema is synthetic and uniform** — identical 6-column
   tables in a single foreign-key chain. Real schemas have irregular tables,
   views, routines, and far messier naming. The raw dump of a real 200-table
   schema would likely be larger, and Strata's answers somewhat larger too.
3. **Token counts are estimated**, not tokenized. Byte counts are exact; the
   ratios are computed from the estimate and would shift slightly under a real
   tokenizer, though not by enough to change the conclusion.
4. **The raw condition is a strawman in one respect**: it assumes an agent
   reads the entire schema. A competent agent with grep might read far less.
   This measures the *upper bound* of what Strata saves, not the realistic
   saving against a well-prompted agent that searches selectively.
5. Seven task types, one database, one session. No variance estimate.

## Qualification added after this record was written

`EV-STRATA-2026-A2B8` found that the `inspect` answers measured here were
**lossy**: unique constraints, check constraints and indexes were omitted. The
figures above were therefore measured against an incomplete answer.

Repairing the omission moved the small-fixture ratio from 2.4x to 2.3x — about
3%. The reduction reported here survives essentially unchanged, and is now known
to be genuine compression rather than an artefact of dropped data. The
large-schema figures were not re-measured after the fix and should be read as
approximately 3% optimistic.

## Counterevidence

Point 4 above is the strongest argument against the headline number: an agent
that greps a schema dump rather than reading it whole would close much of the
gap. Measuring Strata against a *selective-reading* baseline, rather than a
read-everything baseline, is the honest next comparison and has not been done.

## Reproduction or verification notes

Run `tools/spike-d-context-cost.py` with `STRATA_PG` and `STRATA_CORPUS` set.
For the large-schema figures, create 200 tables in a `big` schema with a
foreign-key chain, then compare `pg_dump --schema-only -n big` against
`strata inspect big.entity_100 --json`. Verified 2026-09-11 against PostgreSQL
16.15.
