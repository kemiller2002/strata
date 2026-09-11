---
id: SDE-METHOD-008
title: Repository Semantic Maps and Feature Manifests
status: draft
version: 0.2.0
created: 2026-09-05
updated: 2026-09-05
related_documents:
  - architecture/STRUCTURAL-LOCALITY.md
  - method/NAVIGATION-AND-CONTEXT.md
  - templates/repository-semantic-map.md
  - templates/feature-manifest.md
tags: [method, semantic-map, feature-manifest, navigation]
---

# Repository Semantic Maps and Feature Manifests

Status: REQUIRED routing contract for a nontrivial SDE v0.2 adopting project;
the claim that it reduces engineering cost is EXPERIMENTAL
[`HY-SDE-2026-0008`].

## Purpose

Maps and manifests make ownership discoverable. They are navigation metadata,
not new semantic authorities. Their job is to point to the code, contracts,
tests, and decision records that already govern behavior.

## Repository semantic map

A nontrivial adopting repository MUST expose a small top-level map that lets a
human or agent answer:

1. What are the major semantic areas/features?
2. Where does each live?
3. Which manifest describes it?

The default name is `SDE-MAP.md` at repository root. A repository MAY evolve
an existing architecture/index document instead if the root README or agent
instructions link it unambiguously. Do not create a competing map merely to
use the default filename.

The map should be a routing table, not an architecture essay. See
[`templates/sde/repository-semantic-map.md`](../templates/repository-semantic-map.md).

## Which areas need a manifest

A **meaningful bounded feature** needs a manifest when its state, transitions,
contracts, tests, or dependencies span enough material that ownership is not
obvious from one local entry point. A small repository or trivial component
may use the repository map alone; record the manifest as `not needed` with a
short reason rather than creating ceremony.

The default location is `<feature-boundary>/manifest.md`. Repositories MAY use
another low-complexity convention when the semantic map points to it.

## Feature-manifest contract

Each manifest declares, as applicable:

- feature name and purpose;
- state ownership, including presentation state;
- transition ownership;
- capabilities;
- inbound interfaces;
- outbound interfaces and important effect contracts;
- tests;
- allowed direct dependencies;
- normal modification boundary;
- escalation-required boundaries; and
- invariants and guards.

Use `none` or `not applicable — <reason>` for a genuinely absent category.
Do not silently omit a category whose absence could be mistaken for missing
documentation.

Prefer paths plus symbol/section names when a file contains multiple
authorities. Globs are acceptable for a cohesive test or boundary set, but a
glob must not conceal that ownership is ambiguous.

## Authority and drift rules (REQUIRED)

1. A manifest points to semantic authority; it MUST NOT restate transition
   legality, invariant logic, wire vocabulary, or another executable rule.
2. When authority, a public contract, tests, or allowed dependencies change,
   update the manifest in the same work item.
3. If actual work requires an undeclared dependency, treat the mismatch as an
   architectural finding. Either correct an incomplete manifest or redesign
   the coupling; do not normalize silent drift.
4. Derive manifest fields from reliable build/dependency metadata where
   practical, but do not introduce generation merely to claim automation.
5. A manifest is not proof that a boundary is correct. Execution and
   verification reveal the Actual Context Surface.

## Nested agent instructions

Repository-level agent instructions establish global execution policy.
Feature-local instructions MAY state local ownership, commands, and boundary
rules where tooling supports nested instructions.

Nested instructions MUST link to the feature manifest and semantic authority
rather than copy their rules. If a local instruction and manifest disagree,
stop, use the repository's authority order, and record the drift.

## Default Markdown representation

SDE v0.2 uses Markdown because it supports readable explanations and direct
links with no custom parser or DSL. The template headings are the contract;
projects may use YAML, JSON, or another low-complexity representation if their
existing tooling can provide the same information deterministically.

SDE does not currently claim to mechanically validate semantic completeness.
Missing-map, missing-manifest, undeclared-dependency, ownership-ambiguity, and
test-discoverability checks remain future verification work until a portable
implementation can avoid false confidence.

