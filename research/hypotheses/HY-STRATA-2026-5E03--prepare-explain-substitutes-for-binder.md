---
id: HY-STRATA-2026-5E03
title: PostgreSQL PREPARE/EXPLAIN can substitute for implementing binder logic
status: proposed
confidence: low
created: 2026-09-11
updated: 2026-09-11
created_by_agent: claude
research_area: strata-semantic-binding
tests: []
supersedes: []
superseded_by: []
tags: [strata, hypothesis]
---

# Hypothesis

## Statement

Asking a live PostgreSQL server to PREPARE or EXPLAIN a statement yields enough name and type resolution that Strata need not implement significant binder logic of its own.

## Falsification criterion

PREPARE/EXPLAIN does not expose the resolution detail Strata needs, or requires a live server in contexts where Strata must work offline, or its safety constraints make it unusable as a routine analysis step.

## Why this is a hypothesis and not a requirement

Q-027 asks this directly. It is attractive because it would remove a large implementation burden, which is exactly why it should be tested rather than assumed. EXPLAIN ANALYZE executes the query and is never a harmless validation step.

## Notebook source

Q-027; S13.
