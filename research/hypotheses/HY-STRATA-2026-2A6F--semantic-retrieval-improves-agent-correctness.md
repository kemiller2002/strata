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

## Status — 2026-09-11 (tested, not supported)

**Tested and NOT supported. Remains `proposed`, not rejected.**

`EV-STRATA-2026-D8E1`: six agents, three per condition, identical graded task
set. **15/15 correct in both conditions.** No difference detected. The Strata
condition also used 8.7% more tokens.

The result is a ceiling effect rather than a refutation: with an 8KB context a
capable agent reads everything and misses nothing, so retrieval had no room to
help. Even the designed discriminator — an undeclared invoice↔customer
relationship that the raw condition had to infer from corpus SQL — was answered
correctly by all three raw agents.

What this establishes is a **floor**: the agent thesis does not pay off on small
schemas with small corpora, where Strata is a net cost. It does not test the
regime Strata is designed for.

Per notebook §134, agent context must not now be used as the sole justification
for Strata.

## Status — 2026-09-11 (scale test: contradicted)

`EV-STRATA-2026-D7B2` retested at 3,009 files / 2.37M tokens, with both
conditions interactive.

**The advantage did not survive.** Agents with `grep` answered all four seam
questions correctly, including the `SELECT *` case designed to be Strata-only.
`grep` answered in **15 ms** against Strata's **247,000 ms**. The relationship-
typing win from `EV-STRATA-2026-B6F3` did not reproduce — forced to search
rather than read, the raw agents were *more* disciplined, not less.

Status stays `proposed` rather than `rejected` for one reason: the fixture is
uniformly schema-qualified SQL, which is close to the best case for text search
and the worst case for a resolver. The untested regime — unqualified names,
dynamic SQL, names reused across schemas — is where a resolved graph should
still win.
