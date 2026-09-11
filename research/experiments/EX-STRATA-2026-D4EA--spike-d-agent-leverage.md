---
id: EX-STRATA-2026-D4EA
title: Spike D: agent leverage
status: active
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

**Context-cost half complete, correctness half not started (2026-09-11).**

Ran with its own measurement harness (`tools/spike-d-context-cost.py`), because
this runtime reports token and cost capabilities as *unsupported* to ROS
telemetry and could not supply the numbers.

Result: `EV-STRATA-2026-F4C6`. 46x-101x context reduction at 200 tables, 2.4x
at 4 tables. `HY-STRATA-2026-1E5D` moves to `supported` at `medium` confidence.

Also found: Strata's scope-and-caveats block is larger than the result it
qualifies for point queries (1,703B scope vs 485B answer). Constant per answer,
so amortised at scale, but repeated verbatim across a session — emitting it once
per session would cut a multi-query session's cost by roughly two thirds.
Recorded, not implemented.

**Not done:** the correctness half. Input/output tokens from real agent runs,
tool calls, files opened, incorrect joins, hallucinated columns, repair loops
and task cost are all **not captured** — no agents were run.
`HY-STRATA-2026-2A6F` remains untested.

**Also not done:** comparison against a SELECTIVE-reading baseline. The raw
condition assumes an agent reads the entire schema, which makes the measured
ratio an upper bound rather than a realistic saving.
