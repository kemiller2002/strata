---
id: EV-STRATA-2026-C9A4
title: At 3,009 files Strata's answer is correct but its scope block is 330x larger than the answer, and every query re-indexes for 247 seconds
research_area: strata-scale
evidence_type: test-result
source_title: Strata Spike D part 5 — scale test
source_author: claude
source_uri: 200-table / 3,009-file synthetic corpus; PostgreSQL 16.15
source_date: 2026-09-11
retrieved: 2026-09-11
created_by_agent: claude
confidence: high
supports: []
contradicts: []
related_theories: []
tags: [strata, scale, performance, packaging, spike-d]
---

# Evidence Record

## Evidence summary

Every earlier result came from a context an agent could read end to end. This
test used a corpus where that is impossible: **200 tables, 10 modules, 3,009
SQL files, 9.03 MB — roughly 2.37 million tokens.** A 200k-token window holds
**8.4%** of it.

Three findings, in order of importance:

1. **The capability holds.** Strata found the planted seam exactly: two buried
   `audit` files out of 3,009 reading a column `billing` believes is dead, plus
   a `SELECT *` reader that no text search for the column name could find.
2. **The packaging broke.** The answer was 118,062 bytes of which the result
   was **357 bytes (0.3%)**. The scope block was **99.1%**, almost entirely a
   3,009-entry enumeration of indexed sources.
3. **Indexing is too slow to use interactively.** Each CLI invocation
   re-indexes the whole corpus: **247 seconds**. Eight queries take ~33 minutes.

## Exact claim supported or contradicted

Supports the capability side of `HY-STRATA-2026-2A6F` at scale: the seam
question is answerable, and answerable in a way text search cannot match.

**Contradicts the practical form of `HY-STRATA-2026-1E5D` at scale**, in a way
the earlier 46x–101x measurement concealed. That measurement used a 4-table
snapshot with no corpus. Once a corpus is indexed, the scope block grows with
the corpus, so the per-answer cost grows with the thing the retrieval was
supposed to make irrelevant.

## Relevant excerpt or data

### The answer was correct

```
impact billing.t_07.legacy_settlement_ref
  explicit readers : audit/queries/settlement-recon.sql
                     audit/queries/settlement-trace.sql
  wildcard readers : analytics/queries/billing-dump.sql
  writers          : []
```

Ground truth is exactly those two explicit readers. The wildcard reader is
`SELECT * FROM billing.t_07`, which contains no occurrence of the column name —
`grep legacy_settlement_ref` across the corpus cannot find it.

### The payload was not

```
whole answer            118,062 B
  result                    357 B   ( 0.3%)
  scope                 117,019 B   (99.1%)
    indexedSources      115,599 B   (97.9%)   <- 3,009 entries
```

The honesty machinery was **330x larger than the answer it qualified**.

### After bounding the enumeration

`indexedSources` is now truncated to 25 entries while `sourceCount` and
`sourceRoots` stay exact and complete:

```
whole answer   3,702 B   (32x smaller)
brief mode       831 B
sourceCount    3,009      exact
sourceRoots    all 10 modules, complete
truncated      true       stated explicitly
```

### Indexing cost

| Corpus | Index time per invocation |
|---|---|
| 42 files | under 2 s |
| 3,009 files | **247 s** |

There is no cache. Every command re-parses every file.

## Interpretation

**The scope block was designed at a scale where it could not hurt.** At 4
tables the enumeration was a handful of entries; at 3,009 it is the entire
payload. The design principle — every answer carries what was and was not
analysed — is right, but the *implementation* conflated "state the bound" with
"enumerate the members". A reader needs to know **how much** was analysed and
**which areas**; they do not need 3,009 filenames. Count and roots preserve
`PR-021` completely; the enumeration added nothing but bytes.

This is a general lesson for the design and not only for this field: any part
of the scope that is O(corpus) rather than O(1) will eventually dominate, and
should be summarised rather than listed.

**The indexing cost is the more serious problem.** 247 seconds per query makes
Strata unusable in the interactive loop it is designed for. An agent asking
eight questions waits half an hour. Nothing in the architecture requires this —
the snapshot and the corpus index are both deterministic functions of inputs
that rarely change — but no persistence layer exists (`Q-010`, `Q-011` and
`Q-020` all remain open, and this is the first evidence of what they cost).

Until a cached index exists, **every measured "Strata is cheaper" claim applies
only to the answer payload, not to the wall-clock or compute cost of producing
it.** That qualification belongs on `EV-STRATA-2026-F4C6`.

**The wildcard result is the strongest capability evidence so far.**
`analytics/queries/billing-dump.sql` is `SELECT * FROM billing.t_07`. It is a
genuine reader of the dropped column, it appears in no grep for the column
name, and Strata found it by expanding the wildcard against the catalog. That
is a class of seam error text search cannot reach at any scale.

## Correction — indexing time

The 247 s figure quoted above was the **first, cold-cache** invocation. A
subsequent batch of 8 queries against the same corpus completed in 1,057 s
total. Two of those 8 failed immediately (their output directory had been
removed mid-run by cleanup, an operator error), so the honest per-query figure
over the 6 that actually ran is **roughly 175 s**, and the batch's own arithmetic
of ~132 s per query is an underestimate because it divided by 8.

Best estimate: **~175 s warm, ~247 s cold**, per invocation, at 3,009 files.

This does not change any conclusion. The comparison against `grep` at 15 ms
moves from ~16,000x to roughly **9,000–12,000x**, and the interactive loop
remains unusable: 6 queries still cost about 17 minutes.

## Limitations

1. **The corpus is synthetic and uniform** — 300 near-identical generated files
   per module. Real corpora have more varied SQL, which would change parse cost
   and gap counts but not the O(corpus) scope problem.
2. **13,579 extraction gaps** were reported across the corpus. The generated
   files use CTEs with unqualified column references, which resolve poorly. This
   inflates the gap count and is partly an artefact of the generator, though it
   does reflect a real weakness with CTE-heavy SQL.
3. The 247 s figure is a Debug build on one machine, unprofiled. A Release build
   and obvious caching would both improve it; the order of magnitude is the
   finding, not the number.
4. Agent behaviour at this scale is **not** measured in this record — only the
   tool's own output and cost.

## Counterevidence

None for the findings themselves. The capability result and the packaging
result point in opposite directions and both are real: Strata answered a
question grep cannot, and wrapped the answer in 330x its own weight.

## Reproduction or verification notes

Generate 200 tables across 10 schemas with intra-module FK chains, 3,009 SQL
files averaging ~3KB, and plant a column read by exactly two files in a
different module plus one `SELECT *` reader. Run
`strata impact <schema>.<table>.<column> --json` and measure both the result
size and the whole payload. Verified 2026-09-11.
