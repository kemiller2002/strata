---
id: HY-STRATA-2026-3C81
title: Observed-join inference identifies real undeclared relationships at usable precision
status: proposed
confidence: low
created: 2026-09-11
updated: 2026-09-11
created_by_agent: claude
research_area: strata-relationship-graph
tests: []
supersedes: []
superseded_by: []
tags: [strata, hypothesis]
---

# Hypothesis

## Statement

Repeated equality-join patterns in a SQL corpus identify genuine relationships absent from the catalog, at a precision high enough to be useful for navigation.

## Falsification criterion

Inferred relationships are largely spurious, or their precision is too low for developers to trust, making the feature a false-confidence generator rather than a navigation aid.

## Why this is a hypothesis and not a requirement

S134 and S144.6 both warn that relationship inference may create dangerous false confidence, and that inference should improve navigation rather than create new database truth. The feature is only worth building if measured precision supports it.

## Notebook source

S9; S134; S144.6.
