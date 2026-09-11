---
id: EX-STRATA-2026-B2E8
title: Spike B: parsing versus semantic binding
status: completed
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

**Completed 2026-09-11, both passes.**

*Offline pass* produced `EV-STRATA-2026-B9C4` and `DF-STRATA-2026-9B2E`,
including the CTE-shadowing false-edge finding.

*Live pass* produced `EV-STRATA-2026-C5D2` against PostgreSQL 16.15:
PostgreSQL resolves the shadowed name to the CTE, refuses ambiguous columns,
and folds unquoted identifiers — all matching Strata's model. It also
demonstrated `RK-003`: the pinned 17.5 parser accepts `JSON_TABLE` and
`MERGE ... RETURNING`, which the 16.15 server rejects.

**Exit criterion met** for the qualified/unqualified, CTE, ambiguity,
search_path and folding cases.

**Still open, carried to `Q-027`:** overloaded-function resolution was not
meaningfully tested (the fixture had a single signature, so no ambiguity
existed to observe), and whether `PREPARE`/`EXPLAIN` can substitute for binder
logic is untested. Casts and operator resolution were not compared.
`HY-STRATA-2026-5E03` therefore remains untested.
