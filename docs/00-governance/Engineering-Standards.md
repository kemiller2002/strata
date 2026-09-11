---
id: GV-ENG-001
title: Engineering Standards
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
  - Agent-Operating-Manual.md
tags: [governance, engineering, quality]
---

# Engineering Standards

## Principles

Prefer clarity over cleverness, simple solutions before abstractions, explicit contracts, reversible evolution, and evidence-driven architecture. Treat accessibility, security, privacy, and maintainability as product qualities. Test in proportion to risk. Measure performance before optimizing it.

No stack-specific standard is canonical in Phase 1 because the repository contains no implementation or supported stack evidence. Future local standards may refine this document without weakening it.

## Repository Hygiene and Code Quality

- Keep structure predictable and ownership discoverable. Do not add unexplained generated files, secrets, duplicate canonical documents, or dead code without a recorded reason.
- Make small, cohesive changes; preserve unrelated and uncommitted user work; document migrations and generated-artifact workflows.
- Use clear names, cohesive modules, explicit contracts and error handling, and limited hidden state.
- Avoid speculative abstractions. Prefer code whose behavior can be reasoned about locally.
- Comments explain intent, constraints, or why—not syntax already visible.
- Validate inputs and failure paths at trust boundaries; choose safe defaults.
- Instrument development through the ROS execution contract. Prefer Git, compiler, test, build, static-analysis, and runtime outputs over agent estimates; preserve provenance and declare attribution gaps caused by dirty baselines or unavailable runtime APIs.

## Architecture Decisions

Create a `DF-` Decision Record when a choice establishes or materially changes system boundaries, persistent data shape, public contracts, security model, core dependency/platform, cross-cutting convention, or a migration that is costly to reverse. Record context, decision, alternatives, evidence, consequences, reversibility, status, and follow-up validation. A prototype may precede acceptance if it is isolated and explicitly noncanonical.

## Testing and Validation

Establish a baseline before modification when practical. Select tests according to failure mode and risk:

- unit tests for local logic and edge cases;
- integration tests for component and external-boundary contracts;
- end-to-end tests for critical user journeys;
- accessibility tests plus appropriate human review for affected interfaces;
- visual tests for consequential rendering behavior;
- performance tests for measured budgets or suspected regressions;
- security-focused tests and review for trust boundaries and sensitive changes.

Run the narrowest useful checks during iteration and the affected broader suite before completion. Never infer coverage from unrelated tests. Record skipped, unavailable, flaky, or failing checks and their implications.

## Accessibility

Accessibility is a core acceptance property for user-facing work. Use semantic structure, keyboard-operable interactions, visible focus, perceivable states, sufficient non-color cues, adaptable layouts, and platform accessibility APIs. Test with automated tools and manual methods in proportion to impact. Do not claim conformance to a named standard until the applicable criteria and scope have actually been evaluated; Phase 1 has no repository-specific conformance target.

## Security and Privacy

Apply least privilege; never commit secrets; treat dependencies and updates as supply-chain decisions; validate untrusted input; encode output for its context; avoid sensitive data in logs, fixtures, examples, and prompts; and fail safely. Security/privacy-sensitive changes require explicit threat and data-flow consideration, proportionate specialist review, and the restricted-decision controls in the constitution.

## Documentation

Document public behavior, architectural intent, non-obvious constraints, migration and rollback steps, operational procedures, generation processes, known limitations, and intentionally skipped validation. Keep one canonical source for each rule and link to it. Documentation changes that alter policy follow governance change control.

## Definition of Done

A change is done when its acceptance criteria are met; relevant checks pass; failures and skipped checks are explained; affected documentation and traceability are updated; no known high-severity regression remains; unrelated user work is preserved; and a capable successor can understand the resulting state and remaining risk.
