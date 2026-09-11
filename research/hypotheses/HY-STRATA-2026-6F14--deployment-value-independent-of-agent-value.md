---
id: HY-STRATA-2026-6F14
title: Deployment-safety value is independent of agent-leverage value
status: proposed
confidence: medium
created: 2026-09-11
updated: 2026-09-11
created_by_agent: claude
research_area: strata-product-value
tests: [EV-STRATA-2026-F8A2]
supersedes: []
superseded_by: []
tags: [strata, hypothesis]
---

# Hypothesis

## Statement

Strata's deployment-safety, impact-analysis and drift capabilities justify the semantic model independently, even if the agent-leverage hypotheses fail.

## Falsification criterion

Neither value proposition holds on its own, meaning the semantic model is not justified by either use case separately.

## Why this is a hypothesis and not a requirement

This hypothesis exists so that a negative Spike D result does not invalidate the whole program, and so that a positive one does not excuse skipping deployment-safety evidence. S134 and S144.7 both assert this independence; neither tests it.

## Notebook source

S134; S144.7; S144.15.

## Status — 2026-09-11 (partially tested; stays `proposed`)

`EV-STRATA-2026-F8A2` built a deterministic pre-deployment gate and ran it
against a nine-case matrix: **9/9 correct, byte-identical across three runs,
correct CI exit codes, no agent in the loop.**

**The independence half of this hypothesis is established.** The gate does not
depend on agent leverage in any way, and it provides something an agent
structurally cannot: a pipeline cannot block on a probabilistic answer, because
the same input may not yield the same verdict and there is nothing to diff or
assert on.

**The value half is NOT established, which is why this stays `proposed`.**

1. The matrix is author-written, nine cases, on an 11-file corpus.
2. **No baseline comparison.** The real question is not "does the gate work" but
   "does it beat existing migration tooling plus code review". Never run.
3. **No real migration history passed through it**, so the false-positive rate —
   the number that actually decides whether a gate survives contact with a team
   (`RK-016`, §136) — is unmeasured.
4. **Narrow coverage.** Triggers, views, functions, renames, partitions, RLS and
   privilege changes all fall through to `requires-approval`. Safe, but a gate
   that abstains on most real migrations delivers little. The abstention rate on
   real input is unknown and is the most likely way this fails.
5. Drift detection and schema diff, the other two capabilities this hypothesis
   names, are not built.

**What would settle it:** gate a real migration series from a real repository,
measure the false-positive and abstention rates, and compare against what the
team's existing tooling already catches.
