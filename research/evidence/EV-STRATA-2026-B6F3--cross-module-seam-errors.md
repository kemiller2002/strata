---
id: EV-STRATA-2026-B6F3
title: On cross-module seam questions Strata and raw context each win one task decisively, in opposite directions
research_area: strata-agent-leverage
evidence_type: test-result
source_title: Strata Spike D part 4 — cross-module seam errors
source_author: claude
source_uri: six subagent runs, 2026-09-11; four-module fixture in examples/seams
source_date: 2026-09-11
retrieved: 2026-09-11
created_by_agent: claude
confidence: medium
supports: []
contradicts: []
related_theories: []
tags: [strata, agent-leverage, spike-d, seam-errors, boundary, correctness]
---

# Evidence Record

## Evidence summary

The earlier correctness test (`EV-STRATA-2026-D8E1`) asked **lookup** questions
and hit a ceiling — both conditions scored 15/15. This test asks
**consequence-across-a-boundary** questions, which no single file answers.

The ceiling broke. Two of five tasks discriminated, **in opposite directions**:

| Task | raw | strata |
|---|---|---|
| T1 — which files break if we drop a column | **3/3** | **0/3** |
| T5 — which cross-module relationships are unenforced | **0/3** | **3/3** |

T2, T3, T4 tied at 3/3.

This is the first evidence of a real capability difference in either direction.

## Exact claim supported or contradicted

Partially supports `HY-STRATA-2026-2A6F` for one class of seam question
(relationship typing across a boundary) and **refutes it** for another
(column-level impact). The hypothesis is too coarse as stated: whether semantic
retrieval improves correctness depends on which seam question is asked.

## Method

A four-module fixture — `billing`, `fulfilment`, `crm`, `analytics` — with 42
SQL files and four planted seam defects: a hidden cross-module reader of a
column its owner believes is dead; an undeclared cross-module relationship; a
cross-module write violating ownership; and a wildcard reader across a seam.

Five tasks, ground truth fixed in advance (`docs/strata/experiments/`), three
agents per condition, identical instructions, only the context differing. One
task (T1) was included **deliberately expecting Strata to lose**.

## Relevant excerpt or data

### T1 — Strata lost, decisively

All three raw agents named both breaking files exactly:

> *"`analytics/queries/legacy-audit.sql` … `analytics/queries/legacy-recon.sql`
> … The comment in `billing/migrations/003-drop-legacy.sql` ('Billing believes
> legacy_code is unused') is wrong."*

All three Strata agents answered **NOT DETERMINABLE FROM CONTEXT**:

> *"the context reports readers at file granularity only, never column
> granularity, so no file can be tied to `legacy_code` specifically."*

The cause is a **capability gap, not a reasoning failure**: Strata has no
column-level reader query. `readers billing.invoice` returns all 12 file
readers without isolating which touch the column. Raw context contains the
literal string and can be searched.

Notably, all three Strata agents still refused to certify the drop as safe, and
gave a correct reason grounded in stated uncertainty — citing
`supportsAbsenceClaim: false` and `extractionGaps: 30`. They reached the right
*decision* ("do not drop") without the right *answer* ("these two files").

### T5 — Strata won, decisively

Ground truth is exactly two cross-module unenforced relationships.

All three Strata agents returned **exactly those two**, correctly typed, with
evidence counts, and correctly excluded the intra-module one:

> *"`billing.orders.account_code` → `crm.account.account_code`: observed,
> certainty low, 2 evidence … `billing.orders.order_id` →
> `fulfilment.shipment.order_ref`: observed, certainty medium, 3 evidence …
> (`fulfilment.carrier_rate.carrier` → `fulfilment.shipment.carrier` is also
> unenforced but stays inside the fulfilment module.)"*

All three raw agents **over-reported**, returning three or four items by
conflating *relationship* with *dependency* — adding view dependencies and
"analytics reads billing columns" as though they were unenforced relationships.

### Cost

`raw` mean 50,874 tokens; `strata` mean 52,707 (+3.6%). Strata again cost more
at this scale (16.5KB raw context).

## Interpretation

**The two results are the same mechanism seen from two sides.** Strata answers
from a typed graph: it knows what a *relationship* is, so it returns exactly the
relationships and refuses questions the graph cannot express. Raw context is
text: an agent can find any string in it, but has no type system, so it
improvises a category boundary and improvises it inconsistently.

That trade is favourable exactly where the question is about **kind** —
is this relationship enforced? does this cross a boundary? who owns this
write? — and unfavourable where the question is about **granularity Strata does
not model**.

**T5 is the seam question that matters most** and Strata won it cleanly. "Which
of our cross-module links does the database not actually enforce" is the
question whose wrong answer causes silent production breakage, and the raw
agents answered it with a padded list that mixes enforced view dependencies in
with genuine unenforced joins. A team acting on the raw answer would chase
non-problems and could miss the real ones in the noise.

**T1 is a fixable product gap, not a refutation.** Column-level reader tracking
is already computable — `CatalogResolution.columnDependencies` resolves column
references per statement — it is simply not stored in the graph or exposed as a
query. This is the single highest-value gap the experiment found.

**The epistemic difference is consistent and one-directional.** Across T1, T2
and T4 the Strata agents volunteered bounds the raw agents did not: "this is a
bounded result, not an absence claim", "whether it touches `order_id`
specifically is NOT DETERMINABLE". The raw agents stated conclusions with
uniform confidence whether or not the context justified it. On T1 that made raw
*more useful*; on a question where the corpus was incomplete it would make raw
*more dangerous*. This experiment cannot distinguish those cases — the fixture
corpus is complete by construction.

## Limitations

1. **Still small.** 16.5KB raw context is readable in full. The regime Strata
   targets — where reading everything is infeasible — is untested for
   correctness.
2. **Grading was performed by the author of both the fixture and the code.**
   T5's "over-reported" judgement in particular rests on my reading of what the
   question asked; a raw agent could argue a view dependency *is* an unenforced
   cross-module relationship. The ground truth was fixed before the runs, but I
   wrote it.
3. **Four planted seams, one fixture, one model, three runs per condition.**
4. **The Strata condition was pre-fetched**, not interactive — 16 commands
   bundled. An agent choosing its own queries might do better or worse.
5. T1's result depends on a capability gap that is now scheduled for repair, so
   this specific comparison will not reproduce once column-level readers exist.

## Counterevidence

T1 is direct counterevidence to any general claim that Strata improves seam
correctness: on the single most operationally common seam question — "what
breaks if I drop this column" — Strata was unable to answer and raw was exact.

## Reproduction or verification notes

Fixture: `examples/seams` plus the four-schema DDL. Task set and ground truth in
`docs/strata/experiments/`. Run three agents per condition on prompts differing
only in context. T1 and T5 are the discriminating tasks; T2–T4 tie and can be
dropped from a rerun.
