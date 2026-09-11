---
id: EX-STRATA-2026-E5FB
title: Spike E: diff safety
status: proposed
tests_hypotheses: []
created: 2026-09-11
updated: 2026-09-11
created_by_agent: claude
research_area: strata-schema-diff
tags: [strata, spike, experiment]
---

# Experiment

## Question

Which schema transitions can be derived safely and unambiguously from a desired-versus-actual comparison?

## Method

Build a synthetic schema-evolution matrix and categorise each transition as safe/automatic, unsafe/explicit, or ambiguous. No execution against any database.

## Exit criterion

A categorised transition set, with no execution.

## Status notes

Not started. Blocked on P4 and on Q-003, Q-004, Q-010, Q-020.
