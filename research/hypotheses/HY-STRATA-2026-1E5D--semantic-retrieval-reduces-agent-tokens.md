---
id: HY-STRATA-2026-1E5D
title: Semantic retrieval reduces agent token consumption versus raw SQL corpus reading
status: supported
confidence: medium
created: 2026-09-11
updated: 2026-09-11
created_by_agent: claude
research_area: strata-agent-leverage
tests: [EX-STRATA-2026-D4EA]
supersedes: []
superseded_by: []
tags: [strata, hypothesis]
---

# Hypothesis

## Statement

Giving an agent targeted Strata semantic slices for a database task consumes materially fewer input tokens than giving it raw schema DDL and SQL corpus files for the same task.

## Falsification criterion

Matched tasks run under both conditions show no material reduction in input tokens, or a reduction too small to justify Strata's cost. Notebook S134 is explicit: if agent correctness and cost do not improve materially, agent context must not be the sole justification for Strata.

## Why this is a hypothesis and not a requirement

It is the notebook's most prominent motivation and its least tested claim. Promoting it to a requirement would make the roadmap unfalsifiable: any amount of retrieval machinery could be justified by an unmeasured benefit.

## Notebook source

S134 challenge pass 1; D-020; S144.7.

## Status — 2026-09-11

**Supported for context size, at scale.** `EV-STRATA-2026-F4C6` measured
46x–101x reduction on a 200-table schema and 2.4x on a 4-table one. Raw context
is O(schema size); a Strata point query is O(1) in schema size, so the two
figures are the same mechanism at different points on the curve.

Confidence is `medium`, not higher, because the measurement compares against an
agent that reads the WHOLE schema. An agent that greps selectively would close
much of the gap, and that comparison has not been run. The 200-table schema is
also synthetic and uniform.

This says nothing about whether agents produce better SQL — that is
`HY-STRATA-2026-2A6F`, still untested.
