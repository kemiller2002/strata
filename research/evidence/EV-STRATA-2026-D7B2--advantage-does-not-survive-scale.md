---
id: EV-STRATA-2026-D7B2
title: At 3,009 files the advantage does not survive — grep is ~16,000x faster and agents using it answered every seam question correctly, including the case designed to be Strata-only
research_area: strata-agent-leverage
evidence_type: test-result
source_title: Strata Spike D part 6 — seam correctness at infeasible-to-read scale
source_author: claude
source_uri: 200-table / 3,009-file corpus; two interactive agent runs; PostgreSQL 16.15
source_date: 2026-09-11
retrieved: 2026-09-11
created_by_agent: claude
confidence: high
supports: []
contradicts: [HY-STRATA-2026-2A6F]
related_theories: []
tags: [strata, agent-leverage, spike-d, scale, negative-result]
---

# Evidence Record

## Evidence summary

Every prior result came from a context an agent could read end to end. This one
did not: 2.37M tokens, of which a 200k window holds 8.4%. Both conditions were
therefore made **interactive** — agents with shell tools, searching rather than
receiving.

**The advantage did not survive.**

- `grep` answered the discriminating question in **15 ms**. Strata took
  **247,000 ms** — about **16,000x slower**.
- Both raw agents answered **all four seam questions correctly**, including the
  `SELECT *` case designed to be findable only by Strata.
- The one task Strata won at small scale (relationship typing) **did not
  reproduce** — both raw agents correctly separated true referential
  relationships from mere couplings without help.

## Exact claim supported or contradicted

**Contradicts `HY-STRATA-2026-2A6F` at scale.** The seam-correctness advantage
measured in `EV-STRATA-2026-B6F3` was an artefact of a context small enough to
hand over whole. Given search tools, agents did not need Strata for any question
in this task set.

## Relevant excerpt or data

### The discriminator failed

`analytics/queries/billing-dump.sql` is `SELECT * FROM billing.t_07`. It reads
the dropped column and contains **no occurrence of the column name**. I predicted
`grep` could not find it.

Both raw agents found it, by grepping the **table** name instead of the column
and then reasoning about the wildcard:

> *"One further file is silently affected but will not raise an error:
> `analytics/queries/billing-dump.sql` — it is `SELECT * FROM billing.t_07`, so
> the column vanishes from its result set without a SQL failure."*

That is a **better** answer than Strata's, which lists the file as a wildcard
reader without distinguishing "errors" from "silently changes shape".

### Cost

| Operation | Time |
|---|---|
| `grep -rl legacy_settlement_ref` (finds the 2 explicit readers) | **15 ms** |
| `grep -rl billing\.t_07` (finds all 153 touchers incl. the wildcard) | **15 ms** |
| `strata impact billing.t_07.legacy_settlement_ref` | **247,000 ms** |

Raw agents: 12 and 14 tool calls, 119 s and 176 s wall-clock, for **all four
questions**. Strata needs 247 s for **one**.

### Strata's answers were correct — and that is the problem

```
relationships billing.t_00
  billing.t_00.id -> fulfilment.t_03.billing_order_ref   observed  enforced=false  ev=3
  billing.t_01.parent_id -> billing.t_00.id              declared  enforced=true   ev=1

relationships crm.t_05
  crm.t_05.account_code -> partner.t_11.crm_account_code observed  enforced=false  ev=2
```

Precisely typed, correct, well-evidenced. But `relationships` is **per-object**.
To answer S4 — "list every cross-module relationship that is not enforced" —
a caller must already know which of the 200 tables to ask about, or ask about
all of them: 200 x 247 s ≈ **14 hours**.

The raw agents answered S4 directly by grepping for cross-schema joins.

## Interpretation

**Three distinct problems, only one of which is about correctness.**

1. **No corpus-wide query.** Strata can say everything about an object you name.
   It cannot answer "where are the seams?" — the actual question a team asks.
   Every seam query in this experiment required already knowing the answer's
   location. This is the deepest finding: the retrieval model is object-scoped,
   and seam analysis is portfolio-scoped.

2. **No cached index.** 247 s per invocation, re-parsing 3,009 files every time,
   makes the interactive loop unusable. `Q-010`, `Q-011` and `Q-020` have been
   open since planning; this is what leaving them open costs.

3. **The correctness edge was scale-dependent and vanished.** At 42 files raw
   agents over-reported on relationship typing. At 3,009 files — where they had
   to be systematic rather than reading everything — they did not. Being forced
   to search apparently produced *more* disciplined answers, not less.

**What survives.** Strata's per-object answers remain correct, typed and
evidenced, and its wildcard expansion is real. But "correct when asked the right
question, in 247 seconds" does not beat "correct in 15 milliseconds" for a team
trying to find seams they do not yet know about.

**What this does not show.** `grep` works here partly because the fixture uses
clean schema-qualified SQL. A corpus with unqualified names, dynamic SQL, or
names reused across schemas would degrade text search in ways it would not
degrade a resolved graph. That is the regime where Strata should still win, and
it is untested.

Per notebook §134, and now doubly: agent context must not be Strata's
justification. `HY-STRATA-2026-6F14` — deployment-safety value standing
independently — carries essentially all of the program's weight, and remains
untested.

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

1. **Synthetic, uniformly clean corpus.** Every reference is schema-qualified.
   This is close to the best case for `grep` and probably the worst case for a
   resolver. Real corpora are messier in exactly the ways that favour Strata.
2. **Four questions, two agents, one model.** No variance estimate.
3. **The Strata condition was not run end-to-end by an agent** — the index build
   was still running after 30+ minutes, which is itself the finding, but it
   means no head-to-head agent comparison at scale exists. Strata's outputs were
   inspected directly.
4. Timings are a Debug build, unprofiled. Caching and a Release build would move
   the 247 s substantially; they would not close a 16,000x gap.

## Counterevidence

Strata's own outputs are correct and better-typed than the raw agents' prose,
and its wildcard expansion is genuine. A reader who believes precision of
*representation* matters more than latency could read this record as a case for
Strata with a cache. That reading is available, but it requires the cache and
the corpus-wide query to exist first.

## Reproduction or verification notes

Generate the 200-table / 3,009-file corpus, plant a column read by two files in
another module plus one `SELECT *` reader, then give an agent shell access and
the four questions. Compare against `strata impact` and `strata relationships`.
The decisive measurements are the two `grep` timings against the one `strata`
timing. Verified 2026-09-11.
