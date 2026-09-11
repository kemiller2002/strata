---
id: HY-STRATA-2026-1E5D
title: Semantic retrieval reduces agent token consumption versus raw SQL corpus reading
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

Giving an agent targeted Strata semantic slices for a database task consumes materially fewer input tokens than giving it raw schema DDL and SQL corpus files for the same task.

## Falsification criterion

Matched tasks run under both conditions show no material reduction in input tokens, or a reduction too small to justify Strata's cost. Notebook S134 is explicit: if agent correctness and cost do not improve materially, agent context must not be the sole justification for Strata.

## Why this is a hypothesis and not a requirement

It is the notebook's most prominent motivation and its least tested claim. Promoting it to a requirement would make the roadmap unfalsifiable: any amount of retrieval machinery could be justified by an unmeasured benefit.

## Notebook source

S134 challenge pass 1; D-020; S144.7.
