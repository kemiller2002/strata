---
id: EX-STRATA-2026-D4EA
title: Spike D: agent leverage
status: completed
tests_hypotheses: [HY-STRATA-2026-1E5D, HY-STRATA-2026-2A6F]
created: 2026-09-11
updated: 2026-09-11
created_by_agent: claude
research_area: strata-agent-leverage
tags: [strata, spike, experiment]
---

# Experiment

## Question

Does Strata semantic retrieval measurably outperform raw SQL corpus context for agent database tasks?

## Method

Run matched controlled tasks under two conditions: raw repository SQL context, and Strata semantic retrieval. Measure input tokens, output tokens, tool calls, files opened, retrieval calls, query correctness, incorrect joins, hallucinated columns, repair loops, task cost and completion time. Record unavailable metrics as unavailable, never as zero.

## Exit criterion

Token, cost and correctness evidence sufficient to support or reject the agent-leverage hypotheses.

## Status notes

**Completed 2026-09-11, all three parts.**

1. **Context cost** (`EV-STRATA-2026-F4C6`): 46x–101x reduction at 200 tables,
   2.4x at 4 tables.
2. **Information sufficiency** (`EV-STRATA-2026-A2B8`): retrieval was lossy on
   3 of 9 tasks; fixed; the repair cost ~3%, so the compression is genuine.
3. **Correctness** (`EV-STRATA-2026-D8E1`): six agent runs, **15/15 in both
   conditions, no difference detected**, and Strata cost 8.7% more tokens at
   this scale.

**Outcome: the agent-leverage thesis is not established.** Context reduction is
real at scale; correctness improvement is undemonstrated; and at small scale
Strata is a net cost. Notebook §134 therefore applies: agent context must not be
the sole justification for Strata.

**The most useful open question this leaves** is not a rerun — it is to locate
the crossover schema size at which Strata's context cost drops below raw, then
test correctness at that scale with interactive rather than pre-fetched
retrieval.
