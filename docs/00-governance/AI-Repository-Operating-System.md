---
id: GV-ROS-001
title: AI Repository Operating System
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
  - README.md
  - Agent-Operating-Manual.md
  - Governance-Decision-Log.md
tags: [governance, constitution, lifecycle]
---

# AI Repository Operating System

## Mission and Scope

The ROS is the repository constitution. It keeps justified knowledge, decisions, implementation, validation, and handoffs coherent and inspectable across human and AI contributors. It governs research, engineering, documentation, design, agent behavior, knowledge capture, handoffs, and quality control.

Phase 1 does not define domain theories, product strategy, stack-specific conventions, registry storage formats, deployment operations, or website design. Those require evidence and later canonical records.

Governance identifiers use `GV-<DOMAIN>-NNN`. They are stable identities, not sequence promises; renaming a title does not change its ID.

## Authority Model

When instructions conflict, apply this descending precedence:

1. Explicit user instruction within the user's authority.
2. Applicable safety, legal, privacy, platform, and execution-environment constraints.
3. Canonical governance documents.
4. Accepted domain REPs and accepted theory.
5. Accepted architecture and engineering Decision Records.
6. Current implementation and tests as evidence of actual behavior.
7. Local convention.
8. Agent preference.

Safety and legal constraints are boundaries, not optional instructions, and cannot be overridden by another level. Scope-specific rules refine general rules but do not silently contradict them. Between same-level records, prefer the applicable, accepted, narrower record; if still tied, prefer the newer non-deprecated version. Record material resolutions. Escalate contradictions that materially change the authorized outcome, safety, cost, or irreversible impact.

## Canonical Artifact Model

The primary scientific hierarchy is preserved:

1. **Scientific Research Journal (`JR-`)** — chronological observations, actions, sources, and reasoning; the reconstruction record.
2. **Research Execution Package (`RP-`)** — the completion synthesis and executable handoff for a bounded research effort.
3. **Theory Registry (`TH-`)** — current explanatory models, predictions, confidence, and status.
4. **Evidence Registry (`EV-`)** — durable evidence items with provenance, quality, and claim relationships.

Supporting registries provide indexed state without replacing the hierarchy:

- **Hypothesis (`HY-`)**: falsifiable candidate explanations or predictions.
- **Experiment (`EX-`)**: protocol, execution, observations, and outcome.
- **Decision Record (`DF-`)**: a material choice, alternatives, evidence, and consequences. Legacy uses meaning “decision framework” retain their ID but must declare `legacy_type: decision-framework` and receive a replacement `DF-` Decision Record if used to authorize a choice.
- **Concept (`CN-`)**: stable domain concept and relationships.
- **Glossary (`GL-`)**: term definition and usage boundaries.

Registries are authoritative indexes of accepted/current records; they do not erase the richer source artifacts. Code, documentation, sites, and generated artifacts are implementations or views derived from accepted knowledge and decisions, and must point back when the relationship is material.

## Knowledge Lifecycle

`observation → question → hypothesis → evidence collection → evaluation/experiment → theory update → decision → implementation → validation → handoff → supersession`

The lifecycle may iterate or skip an inapplicable step, but no skip may be disguised. Journals preserve the investigation; REPs synthesize bounded research; registries expose current state; decisions translate justified understanding into action; implementation tests whether the decision works. Implementation results are new evidence, not retroactive proof that the original theory was correct.

## Confidence and Completion

Use a categorical confidence plus an optional numeric estimate:

| Label | Guide | Meaning |
|---|---:|---|
| Low | `0.00–0.39` | weak, indirect, or substantially contradicted support |
| Medium | `0.40–0.69` | meaningful support with material unknowns or alternatives |
| High | `0.70–0.89` | strong, relevant, independently supported evidence |
| Very High | `0.90–1.00` | convergent evidence with serious alternatives tested |

Numbers express judgment, not manufactured precision. State rationale, contradictions, and uncertainty. Confidence is justified belief—not importance, priority, or completion. Completion is separately one of `not-started`, `in-progress`, `partial`, `complete`, or `abandoned`, optionally with a percentage for planning.

## Decision and Change Classes

Assess decisions by reversibility, impact, evidence strength, cost, and safety risk.

| Class | Typical profile | Authority |
|---|---|---|
| D1 Routine | reversible, local, low-risk, low-cost | agent decides and validates |
| D2 Material | reversible but cross-cutting or moderately costly | agent decides with a brief `DF-` note |
| D3 Structural | architectural, difficult migration, high impact/cost | accepted `DF-`; escalate if authorization is unclear |
| D4 Restricted | destructive/irreversible, security/privacy-sensitive, legally ambiguous, or beyond scope | explicit authorization and specialist review where applicable |

Evidence requirements rise with impact and irreversibility. Uncertainty alone does not force escalation when a safe, reversible, in-scope probe can reduce it.

Changes are classified as research-only, documentation, reversible implementation, architectural, data migration, destructive/irreversible, or security/privacy-sensitive. The last four normally require a Decision Record; destructive and sensitive work also requires explicit authorization. A change may occupy multiple classes; use the strictest applicable control.

## Proportional Traceability

- Important claims link to supporting `EV-` and relevant `HY-`/`TH-` records when those systems exist; otherwise cite the inspectable source and declare the gap.
- Theories record evidence for/against, predictions, confidence, and supersession.
- Material choices use `DF-`; experiments use `EX-` and link hypotheses and observations.
- Code changes link a decision, REP, issue, or acceptance criterion when material. Trivial fixes need only a clear change description and tests.
- Generated artifacts identify source inputs and generation method sufficiently to reproduce or update them.
- Each newly begun work item links one or more versioned execution records. Measurements preserve source, collection mechanism, time, unit, execution, and observed/derived/estimated quality; unavailable data is never silently represented as zero.
- Provider-neutral normalized metrics coexist with bounded, privacy-filtered raw runtime telemetry so new provider fields survive until their semantics can be evaluated.

## Completion and Stopping Rules

Stop when the authorized objective and acceptance criteria are satisfied; proportionate quality gates pass; no high-value in-scope issue remains; additional work has diminishing expected value; or a blocker, unsafe action, or missing authorization prevents safe progress. A stop is honest only when failures, skipped checks, open risks, and next actions are recorded. Do not expand scope merely because adjacent improvements exist.

## Status, Versioning, and Supersession

Canonical records use `draft`, `canonical`, `deprecated`, or `archived`. `Draft` is reviewable but non-authoritative; `canonical` governs; `deprecated` remains readable but points to its replacement; `archived` is historical and inactive.

Governance documents use semantic versions: major for incompatible policy or schema meaning, minor for backward-compatible rules/capabilities, patch for clarification with no intended behavior change. Set `supersedes`/`superseded_by` in both directions where possible. Preserve stable IDs across ordinary revisions; use a new ID when a new record must coexist or identity changes. Breaking changes require migration instructions and updates to affected indexes and links.

## Failure and Recovery

- **Conflicting evidence:** preserve both, assess provenance and relevance, lower confidence as warranted, and define a discriminating test.
- **Failed tests:** do not report success; diagnose, revert or isolate unsafe changes when authorized, and record the failure.
- **Unclear state:** stop mutation, inspect status/history/owners, preserve user work, and make the smallest reversible probe.
- **Conflicting instructions:** apply the authority model; record or escalate material ambiguity.
- **Agent error:** disclose impact, preserve evidence, restore safely where authorized, validate recovery, and record prevention.
- **Incomplete prior work:** establish what is actually present and tested; continue only from verified state.
- **Unsafe or impossible task:** do not improvise around safeguards; document the blocker and required authority or resource.

## Anti-Patterns

Prohibited or actively discouraged: hidden assumptions; unsupported certainty; research theater; endless research without a decision or stopping test; implementation without proportionate traceability; preserving a bad system solely because it exists; rewriting canonical history without migration; duplicate sources of truth; claiming validation without testing; and heavy governance for trivial work.
