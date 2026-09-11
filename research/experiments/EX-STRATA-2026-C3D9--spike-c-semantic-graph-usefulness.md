---
id: EX-STRATA-2026-C3D9
title: Spike C: semantic graph usefulness
status: proposed
tests_hypotheses: [HY-STRATA-2026-3C81]
created: 2026-09-11
updated: 2026-09-11
created_by_agent: claude
research_area: strata-relationship-graph
tags: [strata, spike, experiment]
---

# Experiment

## Question

Do extracted declared and observed relationships produce a graph whose path queries match developer knowledge?

## Method

Extract foreign keys and observed joins from a real schema and corpus; build path queries; compare results against developer knowledge of the same database.

## Exit criterion

An accuracy baseline for declared and observed relationship extraction.

## Status notes

Not started. Blocked on S3 and on a real corpus.
