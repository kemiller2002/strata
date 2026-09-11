---
id: EX-STRATA-2026-D4EA
title: Spike D: agent leverage
status: proposed
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

Not started. Blocked on S3 retrieval. Note: this session's ROS telemetry reports token and cost metrics as *unsupported* for this runtime, so the measurement harness cannot rely on ROS telemetry alone and must capture its own.
