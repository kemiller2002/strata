---
id: EV-STRATA-2026-D8E1
title: No agent correctness difference detected between Strata retrieval and raw context; at this scale Strata cost 8.7% more tokens
research_area: strata-agent-leverage
evidence_type: test-result
source_title: Strata Spike D part 3 — agent correctness, six runs
source_author: claude
source_uri: six subagent runs, 2026-09-11; task set and contexts in scratchpad
source_date: 2026-09-11
retrieved: 2026-09-11
created_by_agent: claude
confidence: high
supports: []
contradicts: []
related_theories: []
tags: [strata, agent-leverage, spike-d, null-result, correctness]
---

# Evidence Record

## Evidence summary

Six agents, three per condition, identical five-question task set, identical
instructions; only the context differed.

**Correctness: 15/15 in both conditions. No difference detected.**

**Cost: the Strata condition used 8.7% MORE tokens** (48,870 vs 44,951 mean).

This is a null result on `HY-STRATA-2026-2A6F` and a material qualification of
`HY-STRATA-2026-1E5D`.

## Exact claim supported or contradicted

`HY-STRATA-2026-2A6F` (semantic retrieval improves agent SQL correctness): **not
supported by this experiment**. Also not refuted — see the ceiling effect below.
It remains `proposed`.

`EV-STRATA-2026-F4C6` (46x–101x context reduction): **bounded**. That figure was
one targeted answer against a whole-schema read at 200 tables. This experiment
measured a realistic multi-question session at 4 tables, and Strata lost.

## Method

| Condition | Context |
|---|---|
| **raw** | `pg_dump --schema-only -n sales` plus all 11 corpus `.sql` files — 8,114 bytes |
| **strata** | Pre-fetched output of 7 Strata commands covering the same objects: `inspect` x3, `relationships` x2, `readers`, `writers` — 21,707 bytes |

Both conditions received identical questions and identical instructions: answer
only from the provided context, use no tool beyond one `Read`, and answer
"NOT DETERMINABLE FROM CONTEXT" rather than guessing. Three independent runs per
condition.

Ground truth was fixed in advance (`scratchpad/ground-truth.md`), including a
designed discriminator at Q2.

## Relevant excerpt or data

```
| Condition | Run | Score | Tokens |
| raw       |  1  |  5/5  | 44,957 |
| raw       |  2  |  5/5  | 44,928 |
| raw       |  3  |  5/5  | 44,967 |
| strata    |  1  |  5/5  | 48,881 |
| strata    |  2  |  5/5  | 48,866 |
| strata    |  3  |  5/5  | 48,864 |

raw    mean 44,951 tokens    correctness 15/15
strata mean 48,870 tokens    correctness 15/15
strata: +8.7% tokens
```

**Q2 was the designed discriminator** — the invoice↔customer relationship has no
foreign key, so the raw condition could only find it by reading the corpus SQL
and inferring, while Strata states it directly with
`kind: observed, enforcedByDatabase: false`.

All three raw agents got it right. Example: *"no foreign key exists between
those columns; only a UNIQUE constraint on customer.customer_number exists."*
The discriminator did not discriminate.

## Interpretation

**The experiment could not detect a difference because the task was too easy for
both conditions.** A classic ceiling effect. With an 8KB context, a capable
agent reads everything and misses nothing; there is no room for retrieval to
help. The design assumed the raw condition would struggle to infer an
undeclared relationship from a corpus. It did not — 11 small files are trivially
readable.

**This is informative, not merely inconclusive.** It establishes a floor on when
Strata's agent thesis can pay off: *not* on small schemas with small corpora. At
that scale an agent should simply read the schema, and Strata adds cost. The
8.7% token penalty is the concrete form of that: pre-fetching seven Strata
answers cost more than reading the entire database definition plus every query
in the corpus.

**It does not contradict `EV-STRATA-2026-F4C6`.** That measured one targeted
question against a 200-table schema, where raw context was 229KB. Both results
together describe a crossover: raw context grows with schema size, Strata's does
not, so there is some schema size below which Strata costs more and above which
it costs dramatically less. This experiment sits below the crossover; the
earlier one sits above it. **Neither experiment located the crossover point**,
and that is now the most useful open question about the agent thesis.

**One qualitative difference, not a correctness difference.** Strata-condition
answers used the system's own vocabulary — *"only an observed one … certainty is
low … NOT enforced"* — whereas raw-condition answers reached the same conclusion
as their own inference — *"observed in reports/invoice-join.sql … no foreign key
exists"*. Both correct. Strata's answers are more explicitly grounded in stated
provenance rather than in the agent's reasoning, which may matter for auditability
and for weaker models, but this experiment provides no evidence either way.

Per notebook §134: agent correctness has not improved materially in this test, so
**agent context must not be used as the sole justification for Strata.**
`HY-STRATA-2026-6F14` — that deployment-safety value stands independently — now
carries more of the program's weight, and is itself still untested.

## Limitations

1. **Ceiling effect dominates.** Both conditions at 100% means the instrument
   had no resolution. The result says the task was easy, not that the conditions
   are equivalent.
2. **Scale.** 4 tables, 11 corpus files. The regime where Strata is designed to
   help — hundreds of tables, thousands of queries — was not tested for
   correctness at all.
3. **The Strata condition was pre-fetched, not interactive.** A real agent would
   issue one targeted command, not receive seven. This inflated the Strata
   token count and is the main cause of the 8.7% penalty. A fairer interactive
   design would let the agent choose its queries.
4. **One model, three runs per condition.** No variance estimate worth the name,
   and a single model family. A weaker model might show a difference where this
   one does not.
5. **Author-written task set**, with the same bias noted in
   `EV-STRATA-2026-A2B8`.
6. **Tool-use compliance was instructed, not enforced.** All six agents reported
   a single `Read`, consistent with the instruction, but this was not sandboxed.

## Counterevidence

None required — this is the negative result. The strongest counter-argument to
over-reading it is Limitation 1: a null result from an instrument with no
resolution is weak evidence of equivalence.

## Reproduction or verification notes

Build both context bundles, assemble identical prompts differing only in
context, and run three agents per condition instructed to answer only from the
supplied file. Grade against `scratchpad/ground-truth.md`. The informative next
experiment is not a rerun: it is to locate the crossover schema size at which
Strata's context cost drops below raw, and to test correctness at that scale
with interactive rather than pre-fetched retrieval.
