---
id: HY-STRATA-2026-4D92
title: Catalog snapshot plus AST resolves most references without a live binder
status: proposed
confidence: medium
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

A catalog snapshot combined with parse-tree scope resolution resolves the large majority of object and column references in a real corpus, leaving a small explicitly-marked remainder.

## Falsification criterion

A material fraction of references remain ambiguous or unresolved after offline resolution, making offline analysis too incomplete to support impact claims.

## Why this is a hypothesis and not a requirement

Q-005 asks this directly and it is unanswered. EV-STRATA-2026-B9C4 gives a first partial answer for qualified versus unqualified references but did not measure proportions on a real corpus.

## Notebook source

S144.1; Q-005.
