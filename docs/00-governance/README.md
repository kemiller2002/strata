---
id: GV-INDEX-001
title: Governance Index
status: canonical
version: 1.1.0
owners:
  - repository-governance
created: 2026-07-22
updated: 2026-09-05
review_cycle: quarterly
supersedes: []
superseded_by: []
related_documents:
  - ../../AGENTS.md
  - AI-Repository-Operating-System.md
tags: [governance, index]
---

# Governance Index

This directory is the canonical Phase 1 governance layer for the Repository Operating System. It defines authority, agent conduct, research packaging, engineering quality, and the decisions that established those rules.

## Reading Order and Map

1. [Root agent guide](../../AGENTS.md) — session startup and high-signal rules.
2. [AI Repository Operating System](AI-Repository-Operating-System.md) — constitution, authority, lifecycle, and change control.
3. [Agent Operating Manual](Agent-Operating-Manual.md) — executable research, engineering, escalation, and handoff procedures.
4. Read the task-specific standard: [Engineering Standards](Engineering-Standards.md) and/or [REP Specification](Research-Execution-Package-Specification.md). Executing work also follows the [adaptive telemetry contract](../development-telemetry.md).
5. [Governance Decision Log](Governance-Decision-Log.md) — rationale and revisit triggers for Phase 1 decisions.

Each rule has one authoritative home. The index and `AGENTS.md` summarize and link; the constitution governs cross-domain questions; the manuals govern their named procedures; the decision log explains why but does not override an accepted canonical rule.

## Conflict and Update Policy

Authority descends from explicit user instruction, through applicable safety/legal/platform constraints, canonical governance, accepted domain knowledge, accepted decisions, implementation, local convention, and preference. Full rules are in [the constitution](AI-Repository-Operating-System.md#authority-model). A higher authority never waives applicable safety or legal constraints.

Canonical changes require evidence and impact review, an entry in the decision log, a version/update-date change, repaired cross-links, and a migration or supersession note when behavior or meaning changes. Never rewrite history to conceal a change. Quarterly review is a trigger to inspect, not permission to change without cause.

## New-Agent Entry

Read `AGENTS.md`, follow this reading order only as far as the task requires, inspect current state and applicable domain records, then execute the smallest sufficient workflow. If no domain record exists, state that limitation rather than inventing one.

## Phase and Known Gaps

Phase 1 (Governance Foundation) is canonical as of 2026-07-22. The repository contained only the Phase 1 execution prompt and an initial Git placeholder, so there were no domain REPs, registries, architecture standards, Clarity materials, Visual Engineering materials, or legacy governance documents to reconcile. Phase 2 should establish the repository information architecture and create registries only alongside real records, including their schemas, ownership, and validation.
