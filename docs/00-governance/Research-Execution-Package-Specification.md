---
id: GV-REP-001
title: Research Execution Package Specification
status: canonical
version: 2.0.0
owners:
  - repository-governance
created: 2026-07-22
updated: 2026-07-22
review_cycle: quarterly
supersedes: []
superseded_by: []
related_documents:
  - AI-Repository-Operating-System.md
  - Agent-Operating-Manual.md
  - Governance-Decision-Log.md
tags: [governance, research, rep, specification]
---

# Research Execution Package Specification

## Status and Purpose

This is the canonical normalized REP v2.0 specification. No earlier REP file was present in the repository at adoption, so it preserves and operationalizes the v2 concepts supplied by the Phase 1 governance mandate without claiming migration of absent records.

A Research Execution Package (REP) is the canonical artifact produced at completion—or explicit termination—of a bounded research effort. It is the permanent scientific synthesis, executable handoff, theory update, knowledge-transfer artifact, and synchronization point between autonomous agents. Its success criterion is strict: a different capable agent can reconstruct the investigation, understand current theory, continue immediately, and produce the next REP without additional context.

## Artifact Hierarchy and Boundaries

The canonical scientific hierarchy is:

1. Scientific Research Journal (`JR-`) — chronological, detailed investigation record.
2. Research Execution Package (`RP-`) — assessed synthesis and handoff.
3. Theory Registry (`TH-`) — current explanatory models and predictions.
4. Evidence Registry (`EV-`) — durable evidence and provenance.

Supporting indexes are Hypothesis (`HY-`), Experiment (`EX-`), Decision Record (`DF-`), Concept (`CN-`), and Glossary (`GL-`). They expose current state and relationships without replacing the primary artifacts. `DF-` canonically means Decision Record. A legacy `DF-` that meant decision framework must declare that legacy type and cannot itself authorize a choice; create and link a Decision Record when a choice is made.

A journal is chronological and may contain raw observations, dead ends, and changing interpretations. A REP is a bounded, quality-assessed synthesis that references the journal rather than copying it wholesale. A research result changes justified understanding. An implementation result changes or validates a built system and becomes evidence; it belongs in the REP only insofar as it tests a research claim, with engineering details in their normal change record.

## Identifier Rules

Use `<PREFIX>-<scope>-<sequence-or-unique-token>` where repository conventions exist, or `<PREFIX>-YYYY-NNN` until they do. IDs are unique, stable, case-sensitive, and never reused. Titles may change without changing IDs. Reserve an ID only when creating a real record; gaps are allowed. References use IDs plus links where possible.

| Artifact | Prefix |
|---|---|
| Research Package | `RP-` |
| Journal Entry | `JR-` |
| Evidence | `EV-` |
| Hypothesis | `HY-` |
| Theory | `TH-` |
| Experiment | `EX-` |
| Decision Record | `DF-` |
| Concept | `CN-` |
| Glossary | `GL-` |

## Required Metadata

Every REP begins with machine-readable front matter containing:

```yaml
---
identifier: RP-YYYY-NNN
title: Concise research title
research_area: area
discipline: [primary-discipline]
author_agent: agent-or-human-identity
version: 1.0.0
status: canonical # draft | canonical | deprecated | archived
confidence:
  label: medium # low | medium | high | very-high
  estimate: 0.60 # optional, 0.00–1.00
  rationale: Brief justification
completion:
  state: complete # not-started | in-progress | partial | complete | abandoned
  estimate: 1.0 # optional, 0.00–1.00
priority: medium
related_projects: []
related_documents: []
supersedes: []
superseded_by: []
tags: []
keywords: []
created: YYYY-MM-DD
updated: YYYY-MM-DD
---
```

Confidence follows the ROS model: Low `0.00–0.39`, Medium `0.40–0.69`, High `0.70–0.89`, Very High `0.90–1.00`. Numeric values are optional judgments, not statistical claims unless a method establishes that meaning. Completion is independent of confidence; abandoned work can contain high-confidence findings, and complete work can end with low confidence.

## Research State Snapshot

Every REP begins its body with a compact snapshot:

- Theory Version
- Knowledge Base Version
- Highest Confidence Areas
- Lowest Confidence Areas
- Largest Remaining Unknown
- Active Research Streams
- Recently Invalidated Ideas
- Priority Changes

Use `none`, `unknown`, or `not established` rather than omitting a field or inventing a version.

## Mandatory Sections

The following headings are mandatory. A section may say “Not applicable” with a reason; it may not silently disappear.

1. **Executive Summary** — decision-relevant result, confidence, and largest caveat.
2. **Original Objective** — initial question and success criterion.
3. **Scope** — included, excluded, and changes to scope.
4. **Repository Context** — applicable records, code, history, and baseline.
5. **Current Understanding** — synthesized model and confidence.
6. **Key Discoveries** — findings linked to evidence, hypotheses, and theories.
7. **Evidence Registry** — evidence items added/updated and provenance.
8. **Hypothesis Registry** — hypotheses, evidence for/against, confidence, disposition.
9. **Failed Assumptions** — invalidated or weakened assumptions and impact.
10. **Open Questions** — unresolved, prioritized, and decision relevance.
11. **Recommended Next Research** — highest-value follow-ups and stopping tests.
12. **Research Backlog** — ordered work not selected now.
13. **Suggested Specialized Research Agents** — skills, scope, inputs, outputs; or why none.
14. **Parallel Research Opportunities** — safely independent streams and dependencies.
15. **Risks** — epistemic, operational, ethical, safety, and adoption risks.
16. **Cross-Discipline Opportunities** — useful external methods or perspectives.
17. **Knowledge Relationships** — links among evidence, hypotheses, theories, decisions, concepts.
18. **Theory Impact Assessment** — the required theory-impact fields below.
19. **Research Quality Metrics** — counts/assessments below, with method.
20. **Research Debt** — missing work below, prioritized.
21. **Repository Updates** — durable files/registries changed or explicitly not changed.
22. **Website Updates** — published/generated views changed, deferred, or inapplicable.
23. **AI Consumption Notes** — reliable facts, caveats, retrieval terms, and misuse risks.
24. **Handoff Instructions** — exact resumption point, prerequisites, commands/paths, next action.
25. **Research Journal** — `JR-` index and concise timeline, not a lossy replacement.
26. **Appendix** — methods, extended tables, source notes, or “None.”
27. **Completion Checklist** — every completion item marked with evidence or explanation.

## Evidence Traceability

Decision-relevant claims cite `EV-` and, when applicable, `HY-` and `TH-`. Each evidence item records source/provenance, collection date, method, direct observation versus interpretation, quality/limitations, supported and contradicted claims, and repository location. Preserve contradictory and null evidence. Distinguish primary sources from summaries and independent sources from repeated reporting of one origin. A URL alone is not an assessment.

If registries do not yet exist, include provisional records inside the REP and list creation/update debt; do not fabricate external entries. When registries exist, update them atomically with REP acceptance or state why the update is blocked.

## Theory Impact Assessment

Record all of:

- Affected Theory Records
- Affected Engineering Principles
- New Principle Candidates
- Deprecated Principles
- Confidence Changes
- Predictions Created
- Predictions Invalidated
- Required Theory Registry Updates

For each changed theory, state the prior and new status/confidence, linked evidence, compatibility, and whether the REP proposes or completes the registry update.

## Quality Metrics

Report method-aware counts or `unknown/not measured` for Primary Sources, Independent Sources, Counterexamples Reviewed, Competing Viewpoints Reviewed, Hypotheses Tested, Failed Hypotheses, Research Completeness, Confidence Gain, and Open Questions Reduced. Define the denominator or rubric for percentages. Metrics diagnose quality; they are not targets to game and do not override evidence quality.

## Research Debt

Record and prioritize Missing Evidence, Missing Experiments, Missing Disciplines, Weak Areas, Replication Needed, Tool Limitations, and Assumptions Awaiting Evidence. Include consequence, suggested resolution, and revisit trigger where material.

## Partial, Abandoned, and Superseded Research

- **Partial:** use when useful work exists but the success criterion or planned scope is incomplete. Complete all headings, flag gaps, preserve journal/evidence, and give an executable next step.
- **Abandoned:** use when continuation is no longer justified, safe, authorized, or possible. Record why, salvageable results, confidence, blockers, and conditions for reopening. Abandoned does not mean deleted.
- **Supersession:** a newer REP states what it replaces and why; update links in both records and affected registries. Preserve the old REP as deprecated or archived. Identify claims that remain valid, change, or are withdrawn. Never overwrite historical conclusions as though they were never held.

## Registry Update Rules

On acceptance, update every affected existing registry or explicitly list the blocked/deferred update in Research Debt and Repository Updates. Registry entries are append-oriented in history but may expose a current-state view. State transitions preserve author, date, prior status, reason, and source REP. Do not create an empty registry merely to satisfy this specification. Registry schema and ownership must be canonical before shared use.

## Completion Checklist

- [ ] Metadata is complete and internally consistent.
- [ ] Research State Snapshot is complete.
- [ ] All mandatory sections exist; non-applicable sections explain why.
- [ ] Important claims trace to evidence and relevant hypotheses/theories.
- [ ] Contradictory evidence and failed assumptions are preserved.
- [ ] Theory impact and required registry changes are explicit.
- [ ] Quality metrics state their method or limitation.
- [ ] Research debt is prioritized.
- [ ] Partial/abandoned status, if used, has recovery instructions.
- [ ] Repository and website updates are accurate.
- [ ] Handoff permits continuation without conversation history.
- [ ] Links, identifiers, and supersession relationships validate.
- [ ] Another capable agent can satisfy the REP success criterion.

## Minimal REP Example

This example is structurally abbreviated for illustration; a real REP still includes every mandatory heading.

```markdown
---
identifier: RP-2026-001
title: Example investigation
research_area: example
discipline: [systems-research]
author_agent: example-agent
version: 1.0.0
status: canonical
confidence: {label: medium, estimate: 0.60, rationale: "One primary result; replication pending."}
completion: {state: complete, estimate: 1.0}
priority: medium
related_projects: []
related_documents: [JR-2026-001]
supersedes: []
superseded_by: []
tags: [example]
keywords: [rep]
created: 2026-07-22
updated: 2026-07-22
---
# Research State Snapshot
- Theory Version: TH-EXAMPLE-001 v0.2
- Knowledge Base Version: not established
- Highest Confidence Areas: EV-2026-001 directly supports the observed behavior
- Lowest Confidence Areas: generalization beyond the tested case
- Largest Remaining Unknown: independent replication
- Active Research Streams: none
- Recently Invalidated Ideas: HY-2026-002
- Priority Changes: replication moved to highest priority

# Executive Summary
The tested case supports HY-2026-001 at Medium confidence; broader claims remain unsupported.

# Original Objective
Determine whether the behavior occurs in the defined case. Success: reproducible observation and documented limits.

# Scope
One defined case; generalization excluded.

# Repository Context
Journal: JR-2026-001. No pre-existing registry was available.

# Current Understanding
EV-2026-001 supports HY-2026-001; TH-EXAMPLE-001 remains provisional.

# Key Discoveries
- The behavior reproduced under the recorded protocol [EV-2026-001; HY-2026-001].

# Evidence Registry
- EV-2026-001 — primary observation; protocol and raw result linked; single-case limitation.

# Hypothesis Registry
- HY-2026-001 — provisionally accepted; Medium (0.60).

# Failed Assumptions
- HY-2026-002 rejected: the control did not show the expected effect [EV-2026-002].

# Open Questions
1. Does an independent replication agree?

# Recommended Next Research
Replicate with a preregistered protocol; stop after two consistent independent runs or investigate divergence.
```

## Full REP Template

Copy the metadata schema above, then use this body:

```markdown
# Research State Snapshot
- Theory Version:
- Knowledge Base Version:
- Highest Confidence Areas:
- Lowest Confidence Areas:
- Largest Remaining Unknown:
- Active Research Streams:
- Recently Invalidated Ideas:
- Priority Changes:

# Executive Summary
# Original Objective
## Success Criterion
# Scope
## Included
## Excluded
## Scope Changes
# Repository Context
# Current Understanding
# Key Discoveries
# Evidence Registry
| ID | Claim/Observation | Source and Method | Supports/Contradicts | Quality and Limits |
|---|---|---|---|---|
# Hypothesis Registry
| ID | Statement | Evidence For | Evidence Against | Unknowns | Confidence | Disposition | Implications |
|---|---|---|---|---|---|---|---|
# Failed Assumptions
# Open Questions
# Recommended Next Research
# Research Backlog
# Suggested Specialized Research Agents
# Parallel Research Opportunities
# Risks
# Cross-Discipline Opportunities
# Knowledge Relationships
# Theory Impact Assessment
- Affected Theory Records:
- Affected Engineering Principles:
- New Principle Candidates:
- Deprecated Principles:
- Confidence Changes:
- Predictions Created:
- Predictions Invalidated:
- Required Theory Registry Updates:
# Research Quality Metrics
| Metric | Value | Method/Limit |
|---|---|---|
| Primary Sources | | |
| Independent Sources | | |
| Counterexamples Reviewed | | |
| Competing Viewpoints Reviewed | | |
| Hypotheses Tested | | |
| Failed Hypotheses | | |
| Research Completeness | | |
| Confidence Gain | | |
| Open Questions Reduced | | |
# Research Debt
## Missing Evidence
## Missing Experiments
## Missing Disciplines
## Weak Areas
## Replication Needed
## Tool Limitations
## Assumptions Awaiting Evidence
# Repository Updates
# Website Updates
# AI Consumption Notes
# Handoff Instructions
# Research Journal
# Appendix
# Completion Checklist
```

