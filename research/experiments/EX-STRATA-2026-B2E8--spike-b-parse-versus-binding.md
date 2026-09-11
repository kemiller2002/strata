---
id: EX-STRATA-2026-B2E8
title: Spike B: parsing versus semantic binding
status: active
tests_hypotheses: [HY-STRATA-2026-4D92, HY-STRATA-2026-5E03]
created: 2026-09-11
updated: 2026-09-11
created_by_agent: claude
research_area: strata-semantic-binding
tags: [strata, spike, experiment]
---

# Experiment

## Question

What can be resolved from the parse tree plus a catalog snapshot, and what still requires PostgreSQL server semantic resolution?

## Method

Resolve aliases, tables and columns offline against a catalog snapshot. Test search_path, overloaded functions, casts, operators, quoted identifiers, temporary objects and ambiguous column references. Compare each against live-server behaviour. Represent every outcome as resolved, partially resolved, ambiguous, unsupported or unresolved without collapsing states.

## Exit criterion

A document stating what can be resolved deterministically offline and what needs live PostgreSQL assistance.

## Status notes

**First pass complete, remainder outstanding.** The offline half produced EV-STRATA-2026-B9C4 and DF-STRATA-2026-9B2E, including the CTE-shadowing false-edge finding. The live-server comparison was NOT run: no PostgreSQL server was available in this session. search_path, overload resolution, casts and operator resolution remain untested against a live server.
