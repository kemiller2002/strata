---
id: SDE-METHOD-003
title: Verification Method
status: draft
version: 0.2.0
created: 2026-09-02
updated: 2026-09-05
related_documents:
  - method/CONSTRUCTION-METHOD.md
  - architecture/BOUNDARY-PRESERVATION.md
  - architecture/STRUCTURAL-LOCALITY.md
tags: [method, verification]
---

# Verification Method

Status: RECOMMENDED as a mapping; EXPERIMENTAL as a claim of completeness.

## Core principle

> Every required change obligation should be assigned to the earliest
> trustworthy mechanism capable of knowing it.

This is not the same claim as "the compiler should detect everything." Every
HelixNote trial to date found at least one defect class no layer in this
list catches short of behavioral verification (see below).

## Enforcement mapping

| Failure class | Detected by |
|---|---|
| Missing semantic case | compiler / exhaustiveness checking |
| Architecture dependency violation | architecture check |
| Missing or inconsistent boundary representation | boundary/contract agreement check |
| Persistence disagreement (a schema does not admit what the semantic model allows) | persistence/schema agreement check |
| Present-but-wrong implementation or semantic no-op | behavioral/integration test |
| External/untyped mismatch | runtime validation at re-entry |
| Unknown external outcome | explicit uncertainty state + reconciliation |

## Evidence for each row

- Missing semantic case → compiler: Experiment 1 found 8 of 12 required
  sites (67%) caught this way with zero repository search
  [EV-HN-2026-0003].
- Architecture violation → architecture check:
  `check-semantic-architecture.sh` passed in every trial across all three
  experiments, on both architectures [EV-HN-2026-0003, EV-HN-2026-0004,
  EV-HN-2026-0005].
- Boundary disagreement → boundary/contract check: Target A (SQL CHECK
  agreement) and Target C(b) (field-vocabulary agreement) in Experiment 2
  [EV-HN-2026-0004]; the boundary investigation's BP-002 route-agreement
  script [EV-HN-2026-0002].
- Persistence disagreement → persistence/schema agreement check: Target A
  specifically [EV-HN-2026-0004].
- **Present-but-wrong implementation → behavioral/integration test.** This
  row is the load-bearing exception to "the compiler can catch everything."
  A structurally-present, semantically-inert fan-out arm evaded the
  compiler, the architecture check, Target A, and Target C(b) identically
  in Experiment 2 (Phase 20) and both conditions of Experiment 3 (§17,
  §22) [EV-HN-2026-0004, EV-HN-2026-0005]. Caught only by an integration
  test asserting the real effect happened, confirmed by direct SQL query.
  **This check is REQUIRED for any change that adds or modifies a fan-out
  or dispatch arm whose body is not itself type-checked against its
  semantic input**, not merely recommended.
- External/untyped mismatch → runtime validation: the boundary
  investigation's Deliverable 5 principle — external/untyped data must be
  validated before becoming trusted semantic state [EV-HN-2026-0002].
- Unknown external outcome → explicit uncertainty state: `CommitOutcome`/
  `CorrectionWriteOutcome`-style "definite success / definite failure /
  unknown" reporting, referenced across Experiments 1 and 2's own
  architecture notes.

## What "earliest reliable layer" means in practice

Do not add a slow check (behavioral test, live integration proof) for
something a fast check (compiler, architecture check) can already catch
reliably. Do not rely on a fast check for something evidence shows it
cannot catch (a present-but-inert arm) merely because the fast check
happens to run cleanly.

## Semantic no-op is a first-class defect

A structurally present branch may compile and satisfy architecture and contract
checks while performing no meaningful semantic behavior. Every meaningful
transition, dispatch arm, or boundary behavior SHOULD have evidence of its
intended effect where practical. This evidence may be a focused behavior test,
integration test, representative live proof, or another mechanism that
actually observes the semantic consequence—not merely branch presence.

Exhaustiveness proves that an answer exists. It does not prove that the answer
is behaviorally correct.

## Structural verification

Structural checks guide review; they do not replace semantic analysis.

- `SDE-STRUCT-001` — a source unit crosses a configured physical-line review
  band. The distributed `sde verify` implements this deterministic check as a
  warning. Warnings do not fail an otherwise valid installation.
- `SDE-STRUCT-002` — unusually high declaration concentration.
- `SDE-STRUCT-003` — a source unit appears to span multiple responsibility
  categories.
- `SDE-STRUCT-004` — physical decomposition appears to duplicate semantic
  authority.

Only `SDE-STRUCT-001` is automated in v0.2. The remaining findings require
language-aware or semantic evidence and MUST NOT be presented as mechanically
verified until a reliable implementation exists. Missing map/manifest,
undeclared dependency, cross-boundary edit, test discoverability, and ownership
ambiguity likewise remain review/telemetry findings in v0.2.

Default physical-line review guidance comes from
[`doctrine/STRUCTURAL-LOCALITY.md`](../architecture/STRUCTURAL-LOCALITY.md). A line
count is a proxy; reviewers decide whether a responsibility split, documented
exception, generated-code exclusion, or no change is appropriate.

## Known gap, stated rather than hidden

Experiment 3 found the entire API/HTTP correction tier has **zero**
mechanical guard on either HelixNote architecture tested (BS3-01,
[EV-HN-2026-0005]). Until a mechanism is built and evidenced for that tier,
treat any change reaching the API/HTTP tier as requiring manual
verification at that tier specifically, in addition to whatever mechanical
checks cover the tiers it also touches.

## Completion gate

Before completion, confirm applicable compiler, architecture, boundary,
behavioral, integration, and live checks; record failures and skipped checks;
record unexpected context expansion and architecture findings; and preserve
unavailable evidence as unavailable. A clean structural warning report alone
does not prove semantic locality or behavioral correctness.
