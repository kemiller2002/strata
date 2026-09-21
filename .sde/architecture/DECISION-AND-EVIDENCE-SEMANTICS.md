---
id: SDE-DOCTRINE-009
title: Decision and Evidence Semantics
status: accepted
version: 0.1.0
created: 2026-09-20
updated: 2026-09-20
related_documents:
  - architecture/FOUR-TIER-ARCHITECTURE.md
  - architecture/BOUNDARY-PRESERVATION.md
supersedes: []
superseded_by: []
tags: [doctrine, ordo, decisions, evidence, coverage, state-identity, uncertainty]
---

# Decision and Evidence Semantics

Status: REQUIRED semantics for SDE/Ordo bounded decisions. Executable support may lag this doctrine until GH-18 implementation work completes; where it does, the traceability document must say so explicitly.

## 1. Four-tier preservation is non-negotiable

These semantics refine what Tier 1/2 knows. They do not create a fifth tier and they do not move I/O downward.

- Tier 1 owns semantic state, evidence, coverage, capabilities, obligations, and uncertainty.
- Tier 2 owns pure decisions, guards, policy, transitions, and interpretation of effect results.
- Tier 3 coordinates requests/results and boundary translation.
- Tier 4 performs external effects and observations.
- ROS is outside the application stack and observes history.

If a feature cannot be implemented without violating that direction, revise the feature.

## 2. Minimal semantically complete state view

A bounded decision must operate against the smallest view that is semantically complete for that contract/version and action.

"Semantically complete" means that no omitted authoritative state fact could have a different value and thereby change:

- the legal choice space;
- evidence admissibility or requirement satisfaction;
- required coverage;
- capability applicability;
- obligation satisfaction/blocking;
- policy verdict;
- transition legality.

The domain owns this projection.

Ordo fingerprints the complete selected view. It does not maintain a generic relevance graph and does not selectively ignore changed fields.

State identity must include an explicit view schema/version as well as canonical content. Historical fingerprints are never silently reinterpreted under a new view definition.

If a view is later found insufficient, record the defect, version the view/contract forward, identify affected history where practical, and re-evaluate future actions against the corrected view.

## 3. Scoped coverage

Completeness is meaningful only inside a declared scope.

A reusable coverage claim names a contract/domain-defined scope, a status, and supporting provenance.

The statuses are:

- **Complete**: the declared method fully covered the named scope at the relevant state/time.
- **Partial**: incompleteness is known.
- **Unknown**: completeness has not been established.

Partial and Unknown are never aliases.

One decision may carry multiple coverage claims. Ordo never infers Complete from quantity of context, provider confidence, successful execution, or absence of errors.

Executable Ordo represents coverage as a first-class `ContextCoverageClaim`.

A claim carries a named `CoverageScope`, one of `Complete | Partial | Unknown`, and Evidence IDs that provide provenance for the observation method and facts supporting that claim.

Coverage is not itself Evidence. A `DecisionContract` may require Complete coverage for selected scopes. A request may carry additional Partial or Unknown scopes. Missing, Partial, and Unknown required coverage produce `InsufficientCoverage` before provider execution.

The provider boundary receives coverage explicitly as data. Ordo never parses arbitrary Evidence content to discover coverage and never infers Complete.

## 4. Derived evidence must have valid closure

`EvidenceKind.Derived` forms a derivation relation.

For a supplied closed evidence set:

- every referenced input must exist;
- self-derivation is illegal;
- transitive derivation cycles are illegal;
- shared dependencies are legal;
- transitive closure must be deterministically discoverable.

This rule applies to derivation, not to every historical or domain relationship.

## 5. Unknown external effects require reconciliation

Unknown is not Failed.

After a state-changing external effect with an unknown result:

- keep the outcome explicitly Unknown;
- retain a reconciliation obligation;
- do not blindly retry/compensate if safety depends on whether the effect occurred;
- observe the external system before deciding the next legal action.

Retry before reconciliation is allowed only when the real external contract proves retry safety for that operation. Even then, reconciliation remains necessary before any later action that requires knowing what occurred.

Tier 1/2 expresses the state and obligation. Tier 3/4 performs the probe.

## 6. Capability is semantic authority, not authentication

Ordo Capability means:

> a semantic authority prerequisite that the host/application has established and supplied for transition evaluation.

It is not a token, credential, signature, object-capability, identity proof, or cryptographic security mechanism.

Provider output and confidence can never create authority.

## 7. Negative knowledge is scoped evidence

Do not collapse these states:

- unavailable;
- not searched / not observed;
- not compared;
- not modelled / unsupported;
- unverifiable / unknown;
- absent.

A reusable negative observation identifies its target, scope, method/query, observation time, state/commit/revision, coverage, exclusions, and errors.

Executable Ordo represents the reusable core as a typed `NegativeObservation` carried inside ordinary Evidence content. Evidence continues to own identity, source, observed time, and basis. `ContextCoverageClaim` continues to own Complete / Partial / Unknown and cites the Evidence ID.

`NegativeKnowledge.supportsAbsence` reports structural eligibility only when the observation scope matches the coverage scope, that Evidence is coverage provenance, and coverage is Complete.

"Not found" supports Absent only when the method could detect the target and coverage is Complete for the relevant scope.

The type does not represent searches that never ran, failed reads whose scope was not established, not-compared, not-modelled, unsupported, unverifiable, or domain fields whose source simply omitted a value. Domains retain those richer states. Ordo does not impose a universal negative-knowledge enum.

## 8. What is deliberately not added

This doctrine does not introduce:

- a generic EvidenceRelation ontology;
- a knowledge graph or graph database;
- a selective fingerprint engine;
- an assumption truth-maintenance engine;
- a universal external-effect workflow engine;
- provider voting/consensus;
- automatic Deliberate -> Decide -> Compute promotion;
- automatic ROS-to-Ordo self-modification.

## 9. Measurement objective

SDE/Ordo should continue to test whether narrow typed decisions produce the system-level benefits sought by constrained-decision approaches:

- fewer malformed/unbounded outputs;
- lower cost/tokens/latency per resolved decision;
- more work safely expressed as Compute or bounded Decide;
- explicit escalation rather than forced answers;
- measurable later correctness and calibration.

These are empirical objectives, not doctrine claims. ROS collects the evidence; Ordo does not assume the result.
