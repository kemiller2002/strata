---
id: EV-STRATA-2026-A2B8
title: Targeted retrieval was lossy on three of nine tasks; fixing the loss cost almost nothing, so the context reduction is real
research_area: strata-agent-leverage
evidence_type: test-result
source_title: Strata Spike D part 2 — information sufficiency
source_author: claude
source_uri: tools/spike-d-information-sufficiency.py; PostgreSQL 16.15 local instance
source_date: 2026-09-11
retrieved: 2026-09-11
created_by_agent: claude
confidence: high
supports: []
contradicts: []
related_theories: []
tags: [strata, agent-leverage, spike-d, information-loss, retrieval]
---

# Evidence Record

## Evidence summary

A context reduction is worthless if the compact answer omits what is needed to
answer correctly — retrieval that is small because it is **lossy** rather than
because it is **targeted**. That is the inverse of `NG-010` and it had not been
tested.

A nine-task sufficiency check found Strata **insufficient on three tasks** where
the raw DDL was sufficient. All three omissions were in `inspect`. Repairing
them cost **~141 tokens across seven tasks** — the measured ratio moved from
2.4x to 2.3x.

That last number is the important one: the compression measured in
`EV-STRATA-2026-F4C6` was **not** an artefact of dropping information.

## Exact claim supported or contradicted

Qualifies `EV-STRATA-2026-F4C6`. The reduction reported there was measured on a
lossy `inspect`; this record establishes that the loss accounted for
approximately 3% of the payload, so the headline reduction survives essentially
unchanged.

Does **not** test `HY-STRATA-2026-2A6F`. Sufficiency is a *precondition* for
correctness — an agent cannot be right from a context lacking the facts — but
sufficiency is not correctness.

## Method

A graded task set of nine questions. Each names the Strata command that should
answer it and the ground-truth facts a correct answer depends on. For each
condition, every required fact is checked for presence. Deterministic; no
agents.

Harness: `tools/spike-d-information-sufficiency.py`.

## Relevant excerpt or data

### Before the fix

```
question                                            raw    strata
What columns does sales.orders have?                yes    yes
What type is sales.orders.total?                    yes    yes
Which table does sales.orders reference?            yes    yes
What values are allowed in sales.orders.status?     yes    NO    <- check constraint
Is sales.orders.customer_id indexed?                yes    NO    <- indexes
Is sales.customer.customer_number unique?           yes    NO    <- unique constraint
Which SQL files read sales.orders?                  yes    yes
Is sales.invoice related to sales.customer?         yes    yes
Is the invoice-customer link enforced?              yes    yes

raw sufficient: 9/9    strata sufficient: 6/9
```

### After the fix

```
raw sufficient: 9/9    strata sufficient: 9/9
```

### Cost of the repair

```
small fixture, 7 tasks:
  before (lossy):    ~5,074 tokens    ratio 2.4x
  after (complete):  ~5,215 tokens    ratio 2.3x
```

## Interpretation

**The defect.** `Retrieval.tableSummary` rendered name, kind, scope, columns,
primary key and foreign keys. Unique constraints, check constraints and indexes
were **already in the semantic model and already populated by introspection** —
they were simply never rendered. Three ordinary developer questions were
therefore unanswerable from Strata while trivially answerable from a schema
dump. For a tool whose pitch is "ask a targeted question instead of reading the
schema", that is a serious hole.

**Why it matters beyond the three tasks.** Every one of the omissions bears on
safety, not just convenience:

- a check constraint says what values a column accepts, which is what an agent
  needs before proposing an `INSERT`;
- an index says whether a predicate is cheap, and a *partial* index says the
  cheapness is conditional;
- a unique constraint is what makes an `UPDATE ... WHERE key = ?` bounded to one
  row, which is exactly the distinction the effect classifier turns on.

An agent told "status is text" without the check constraint will write invalid
SQL and be surprised.

**Why the cost number matters.** Had the repair moved the ratio materially, the
honest conclusion would have been that Strata's advantage was partly an artefact
of omission. It moved it by roughly 3%. The compression is genuine: Strata's
answers are small because they are scoped to one object, not because they are
incomplete.

## Limitations

1. **Nine tasks, one fixture.** The task set was written by the same author as
   the code, which is a real bias: questions Strata happens to answer well are
   over-represented by construction. A task set drawn from real developer
   questions would be stronger evidence.
2. **Regex presence, not comprehension.** A fact counts as present if a pattern
   matches. That a string appears does not prove an agent would use it
   correctly.
3. **Still not agent correctness.** No agents were run.
4. **Other commands not audited for loss.** Only `inspect` was found lossy
   because only `inspect` was probed in depth. `relationships`, `path`,
   `readers` and `writers` passed the tasks aimed at them, but a wider task set
   could well find similar omissions — the method here is the useful output, not
   the clean bill of health.

## Counterevidence

None for the loss itself; it was reproducible and is now fixed with regression
tests. The bias noted in Limitation 1 is the strongest reason to treat the 9/9
result as provisional.

## Reproduction or verification notes

Run `tools/spike-d-information-sufficiency.py` with `STRATA_PG` set. To
reproduce the original defect, remove `uniqueConstraints`, `checkConstraints`
and `indexes` from `Retrieval.tableSummary` and observe 6/9. Verified
2026-09-11 against PostgreSQL 16.15.
