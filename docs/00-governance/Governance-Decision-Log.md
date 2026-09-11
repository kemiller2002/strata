---
id: GV-DEC-001
title: Governance Decision Log
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
  - AI-Repository-Operating-System.md
  - Research-Execution-Package-Specification.md
  - ../../prompts/Codex-Prompt-ROS-Phase-1-Governance-Foundation.md
  - ../development-telemetry.md
tags: [governance, decisions, phase-1]
---

# Governance Decision Log

## Evidence Baseline

On 2026-07-22, repository discovery found the Phase 1 execution prompt as the only substantive working-tree document and `.gitattributes` as the only tracked file. There were no existing REPs, registries, journals, agent charters, Clarity or Visual Engineering materials, architecture documents, development standards, or prior governance proposals. Consequently, the decisions below are grounded in the explicit Phase 1 mandate, general operational tradeoffs, and internal consistency—not falsely attributed legacy practice. Each is revisited when real use supplies contrary evidence.

## DF-GOV-001 — Authority Order

- **Date:** 2026-07-22
- **Status:** accepted
- **Context:** Agents need deterministic conflict resolution without treating repository state or agent preference as policy.
- **Hypothesis:** A layered authority order, with safety/legal constraints as non-waivable boundaries, permits useful autonomy while preserving control.
- **Evidence considered:** Required authority order in the Phase 1 mandate; need to preserve explicit user intent; current implementation may reveal actual behavior but not normative intent. No contradictory repository record exists.
- **Alternatives:** Treat implementation as authoritative; put all constraints above user intent without qualification; escalate every conflict.
- **Decision:** Use the eight-level order in `GV-ROS-001`; safety/legal/platform constraints remain applicable boundaries regardless of phrasing. Resolve same-level ties by applicability, specificity, accepted status, then recency.
- **Confidence:** High (0.86)
- **Consequences:** Agents can resolve routine conflicts; material ambiguity still escalates.
- **Revisit trigger:** A real conflict cannot be resolved safely or a platform policy requires a revised formulation.

## DF-GOV-002 — Canonical Artifact Hierarchy

- **Date:** 2026-07-22
- **Status:** accepted
- **Context:** The mandated REP foundation explicitly prohibits silently replacing its hierarchy.
- **Hypothesis:** Journal → REP → Theory Registry → Evidence Registry should remain the primary hierarchy, with other registries supporting it.
- **Evidence considered:** Explicit Phase 1 canonical REP foundation; distinct chronological, synthesis, model, and provenance roles. No legacy artifacts contradict it.
- **Alternatives:** Make REP the sole source; place every registry at the same level; make code the source of research truth.
- **Decision:** Preserve the four-part hierarchy. Hypothesis, Experiment, Decision, Concept, and Glossary records are supporting indexes with distinct lifecycles.
- **Confidence:** Very High (0.95)
- **Consequences:** Rich source records and current-state indexes coexist without claiming equal function.
- **Revisit trigger:** Operational use reveals unresolvable ownership or synchronization problems.

## DF-GOV-003 — Meaning of `DF-`

- **Date:** 2026-07-22
- **Status:** accepted
- **Context:** The prompt identifies ambiguity between decision framework and decision record.
- **Hypothesis:** One decision-record meaning produces stronger traceability than an identifier that denotes both a reusable method and an enacted choice.
- **Evidence considered:** Required preference for one canonical meaning; decisions require context, alternatives, evidence, and consequences. No legacy `DF-` records exist.
- **Alternatives:** Use `DF-` for both; reserve it for frameworks and create a new decision prefix.
- **Decision:** `DF-` means Decision Record. Future reusable decision frameworks require a separately governed artifact type. Legacy `DF-` frameworks retain IDs, declare `legacy_type`, and link a new Decision Record when used.
- **Confidence:** High (0.90)
- **Consequences:** Every `DF-` denotes an actual choice; migration preserves history.
- **Revisit trigger:** Existing external records surface whose migration cost or semantics make this unsafe.

## DF-GOV-004 — Confidence Representation

- **Date:** 2026-07-22
- **Status:** accepted
- **Context:** Contributors need readable uncertainty without false precision.
- **Hypothesis:** Mandatory categorical labels plus optional numeric estimates balance communication and comparison.
- **Evidence considered:** Phase 1 preference for both forms; numerical estimates can imply unjustified calibration; categories alone can be coarse.
- **Alternatives:** Numeric only; categorical only; no standardized representation.
- **Decision:** Require Low, Medium, High, or Very High with rationale; permit `0.00–1.00` estimates. Keep confidence independent of completion, priority, and importance.
- **Confidence:** High (0.84)
- **Consequences:** Uncertainty is visible and comparable while rationale remains primary.
- **Revisit trigger:** Calibration data supports different bands or users consistently misinterpret the scale.

## DF-GOV-005 — Full REP Threshold

- **Date:** 2026-07-22
- **Status:** accepted
- **Context:** A REP is valuable but too costly for every edit.
- **Hypothesis:** Requiring a REP for theory-relevant, decision-relevant, multi-session/agent, or durable research handoffs preserves knowledge without blocking routine work.
- **Evidence considered:** REP purpose and success criterion; explicit efficiency principle and instruction that not every implementation needs a REP.
- **Alternatives:** REP for every change; REP only on publication; agent discretion without thresholds.
- **Decision:** Apply the five artifact thresholds in `GV-AGENT-001`; use a full or partial REP when bounded research affects theory/material decisions, crosses sessions/agents, or requires executable research handoff.
- **Confidence:** High (0.85)
- **Consequences:** Trivial work remains lightweight; substantial research remains reconstructable.
- **Revisit trigger:** Repeated knowledge loss below the threshold or ceremony exceeds its demonstrated value.

## DF-GOV-006 — Agent Escalation Thresholds

- **Date:** 2026-07-22
- **Status:** accepted
- **Context:** Excess escalation blocks work; unbounded autonomy creates harm.
- **Hypothesis:** Agents should decide reversible, in-scope matters and escalate restricted consequences rather than mere uncertainty.
- **Evidence considered:** Phase 1 autonomy and escalation requirements; reversibility limits downside and safe probes reduce uncertainty.
- **Alternatives:** Ask before every choice; fully autonomous action within technical access; dollar-value-only thresholds.
- **Decision:** Escalate irreversible destruction, material security/privacy risk, legal ambiguity, materially conflicting goals, missing required access, or consequences outside authorized scope. Take safe reversible paths and document assumptions.
- **Confidence:** High (0.88)
- **Consequences:** Agents act efficiently while retaining human control over consequential risk.
- **Revisit trigger:** An incident shows a missing risk class or routine work is repeatedly blocked.

## DF-GOV-007 — Registry Update Expectations

- **Date:** 2026-07-22
- **Status:** provisionally accepted
- **Context:** Registries must expose current knowledge without losing history, but none exist yet.
- **Hypothesis:** Append-oriented history plus mutable current-state views provides auditability and usability.
- **Evidence considered:** Need for lifecycle traceability; instruction not to create empty registries; absence of actual registry storage or consumers.
- **Alternatives:** Immutable registries only; overwrite entries; defer all rules until implementation.
- **Decision:** Preserve state-transition history and update affected current entries atomically with accepted REPs when registries exist. Explicitly record blocked updates. Do not create empty registries; Phase 2 must define schema, storage, ownership, and validation with real records.
- **Confidence:** Medium (0.68)
- **Consequences:** A coherent default exists without prematurely choosing file formats.
- **Revisit trigger:** First registry implementation, concurrency needs, or evidence that append history is unsustainable.

## DF-GOV-008 — Versioning and Supersession

- **Date:** 2026-07-22
- **Status:** accepted
- **Context:** Canonical changes must remain legible and migratable.
- **Hypothesis:** Stable IDs, semantic document versions, explicit statuses, bidirectional supersession, and migrations for breaking changes prevent silent policy drift.
- **Evidence considered:** Required metadata and migration rules; auditability needs. No legacy IDs require retrofit.
- **Alternatives:** Date versions only; overwrite in place; always create a new ID.
- **Decision:** Adopt `draft/canonical/deprecated/archived`, semantic versions, stable identity across revisions, new IDs for coexisting/new identities, and explicit migration for breaking changes.
- **Confidence:** High (0.87)
- **Consequences:** Consumers can determine authority and change impact; maintainers incur modest metadata upkeep.
- **Revisit trigger:** Tooling requires a different version scheme or identity ambiguity emerges.

## DF-GOV-009 — Research and Implementation Relationship

- **Date:** 2026-07-22
- **Status:** accepted
- **Context:** Research conclusions must guide implementation without treating a successful build as proof of every underlying theory.
- **Hypothesis:** Decisions bridge accepted research to implementation; validation results feed back as evidence.
- **Evidence considered:** Required end-to-end lifecycle and traceability; implementation can validate behavior while leaving causal explanations uncertain.
- **Alternatives:** Direct REP-to-code with no decision; implementation as canonical truth; isolate research from engineering.
- **Decision:** Material implementation follows an accepted decision linked to relevant REP/theory; its validation produces evidence and may update hypotheses/theory. Trivial implementation may link acceptance criteria without a REP.
- **Confidence:** High (0.89)
- **Consequences:** Traceability is proportional and learning becomes cyclical.
- **Revisit trigger:** Workflow evidence shows the decision bridge duplicates another authoritative mechanism.

## DF-GOV-010 — Role of Root `AGENTS.md`

- **Date:** 2026-07-22
- **Status:** accepted
- **Context:** Every coding agent needs a routinely readable entry point, but a single file cannot carry all procedures.
- **Hypothesis:** A concise root guide is sufficient for orientation when it links to scoped canonical documents, not when it duplicates them.
- **Evidence considered:** Explicit Phase 1 requirement; long startup files are less likely to be followed; scoped documents reduce duplication.
- **Alternatives:** Put the entire ROS in `AGENTS.md`; omit root guidance; tool-specific instruction files as independent authorities.
- **Decision:** `AGENTS.md` is the canonical startup router and high-signal rule summary. Detailed authority remains in the linked constitution and manuals; future local agent files may refine task context but cannot contradict higher governance.
- **Confidence:** High (0.86)
- **Consequences:** New agents orient quickly; canonical detail remains maintainable.
- **Revisit trigger:** Agent evaluations show missed rules, broken discovery, or tool incompatibility with links/front matter.

## DF-GOV-011 — Adaptive Execution Telemetry Contract

- **Date:** 2026-09-05
- **Status:** accepted
- **Context:** Work attribution and evidence existed, but ROS could not represent multiple runtime executions, distinguish unavailable telemetry from zero, or retain new provider fields.
- **Hypothesis:** An automatic, provider-neutral execution envelope with a normalized registry and bounded raw extension layer improves traceability without making runtime-specific fields canonical.
- **Evidence considered:** `EV-ROS-2026-A011`; current CLI/bootstrap/schema architecture; official Codex, Claude Code, Gemini CLI, and Copilot runtime documentation; dirty-worktree attribution limits.
- **Alternatives:** Token fields on work items; provider-native schemas in core; OTel-only records; raw-only logs; automatic vendor hooks.
- **Decision:** Apply `DF-ROS-2026-A010`: begin/finalize execution telemetry with work transitions, keep provider adapters at the edge, preserve measurement provenance/quality and unknown raw fields, require factual multi-valued work/R&D classification, and refuse untrustworthy dirty-baseline change attribution.
- **Confidence:** High (0.86)
- **Consequences:** Deterministic collection is inherited mechanically; detailed runtime and research facts remain capability-dependent. Records grow but are segmented and bounded. Historical work is not rewritten.
- **Revisit trigger:** Cross-provider pilots reveal incompatible semantics, runtime hook security changes materially, or record volume requires an external retention tier.
