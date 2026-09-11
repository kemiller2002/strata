---
id: HY-STRATA-2026-2A6F
title: Semantic retrieval improves agent SQL correctness
status: proposed
confidence: low
created: 2026-09-11
updated: 2026-09-11
created_by_agent: claude
research_area: strata-agent-leverage
tests: []
supersedes: []
superseded_by: []
tags: [strata, hypothesis]
---

# Hypothesis

## Statement

Agents given Strata semantic slices produce SQL with fewer incorrect joins and fewer hallucinated columns than agents given raw corpus context.

## Falsification criterion

Matched tasks show no material difference in incorrect joins, hallucinated columns, or repair loops. Token reduction alone does not satisfy this hypothesis; it is measured separately.

## Why this is a hypothesis and not a requirement

Correctness and cost can move independently, and S144.7 requires the agent value proposition be measured separately from the deployment value proposition. Either may succeed while the other fails.

## Notebook source

S14; S137; D-020.
