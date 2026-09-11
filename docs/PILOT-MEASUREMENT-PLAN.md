---
id: PILOT-MEASUREMENT-strata
title: Strata ROS Pilot Measurement Plan
status: draft
version: 0.1.0
created: 2026-09-11
updated: 2026-09-11
---

# Strata ROS pilot measurement plan

## Evaluation question

Does the ROS greenfield profile improve decision quality, traceability,
handoff continuity, and avoidable rework enough to justify its operating cost?

## Baseline

Define before implementation. Prefer either a comparable prior project or a
lightweight workflow specified before results are known. Record material
differences that prevent direct comparison.

## Measures

| Measure | Operational definition | Collection point |
|---|---|---|
| Time to first accepted vertical slice | Elapsed working time from scope freeze to acceptance | milestone |
| Decision trace coverage | Material decisions with rationale, alternatives, and evidence / all reviewed material decisions | review |
| Assumption escape count | Material assumptions discovered only after implementation began | continuous |
| Rework | Time spent reversing avoidable decisions or recreating missing context | continuous |
| Handoff reconstruction | Time and missing questions for a new operator to continue | handoff test |
| Operating overhead | Time spent maintaining ROS-specific artifacts and checks | continuous |
| Outcome quality | Slice-specific user or system acceptance measures | milestone |

## Falsification conditions

The pilot should count against adoption if:

- operating overhead is material and no decision, handoff, or rework benefit is
  observed;
- records cannot reconstruct why consequential choices were made;
- artifacts repeatedly drift from implementation;
- the process delays risk discovery rather than accelerating it; or
- outcome quality is indistinguishable while cost is materially higher.

## Decision rule

At the first milestone, choose one: continue unchanged, simplify, revise,
pause, or reject. Document the evidence and uncertainties; do not convert a
single successful project into a general discipline claim.
