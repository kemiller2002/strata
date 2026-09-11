---
id: EX-STRATA-2026-A1F7
title: Spike A: PostgreSQL parser viability
status: completed
tests_hypotheses: []
created: 2026-09-11
updated: 2026-09-11
created_by_agent: claude
research_area: strata-parsing
tags: [strata, spike, experiment]
---

# Experiment

## Question

Can a libpg_query-based .NET wrapper parse representative PostgreSQL SQL well enough for Strata, and which wrapper?

## Method

Integrate candidate wrappers into an F# project; parse a representative corpus covering SELECT, INSERT, UPDATE, DELETE, joins, CTEs, recursive CTEs, aggregates, window functions, subqueries, LATERAL, arrays, JSON, RETURNING, UPSERT, DDL, views, functions, triggers, quoted identifiers, schema-qualified names; parse PL/pgSQL; test parse errors; inspect AST ergonomics from F#; check licence, target frameworks and native platform coverage.

## Exit criterion

Parser chosen or rejected with evidence.

## Status notes

**Completed 2026-09-11.** Result: pgsqlparser 1.0.0 selected. Evidence EV-STRATA-2026-7A31; decision DF-STRATA-2026-4C7A. Not executed on Windows or macOS; only linux-x64.
