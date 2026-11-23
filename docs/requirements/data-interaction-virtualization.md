# Universal Data Interaction Virtualization Requirements

Status: Proposed baseline requirements for implementation and architectural review
Identifier: STRATA-DIV-001
Scope: Echelon-wide, with the DAF platform as the first comprehensive consumer

## Objective
Every application data interaction must be testable without live external services. The same application contracts must support live, deterministic mock, sanitized recorded replay, coordinated scenario, hybrid, and fault-injection modes. Mocking must represent behavior and state, not just response payloads.

## Functional requirements
- DIV-001: Define machine-readable contracts for every external interaction, including request/response shapes, errors, side effects, authorization, idempotency and event semantics.
- DIV-002: Route calls through replaceable adapters without requiring changes to application UI or domain logic.
- DIV-003: Support live, mock, replay, scenario, hybrid and fault-injection modes at dependency and tenant scope.
- DIV-004: Provide seeded deterministic simulations and portable scenario manifests with explicit versions.
- DIV-005: Maintain coherent simulated state across entities, transactions, balances, workflows and events.
- DIV-006: Provide virtual time for scheduled grants, 180-day charity reverification, monthly statements, seven-year document retention and other time-based processes.
- DIV-007: Simulate latency, rate limits, timeouts, partial failure, provider outages, retries, duplicates, event reordering and eventual consistency.
- DIV-008: Record and replay interactions only after redaction/tokenization and explicit controls preventing production secrets or donor data from entering fixtures.
- DIV-009: Provide contract drift checks against controlled real-provider integration tests; passing mock tests alone must never establish production compatibility.
- DIV-010: Integrate with Dokimos for contract, state-machine, property-based, regression, accessibility and UI-state testing; preserve reproducible evidence and seed.
- DIV-011: Expose structured agent interfaces for creating, executing, inspecting and challenging scenarios.
- DIV-012: Provide a Forma-based Scenario Studio for authoring, previewing, comparing, exporting and replaying scenarios.
- DIV-013: Enforce tenant isolation, authorization boundaries, environment safety and explicit visible indication of simulated data.
- DIV-014: Support snapshot/restore, reset and reproducible cross-service orchestration.
- DIV-015: Ensure all new external integration contracts ship with a mock adapter and automated contract tests before being considered complete.

## DAF acceptance scenarios
1. A recurring $10 grant executes every simulated pay period, respects funding and client policies, and reconciles the ledger.
2. A charity's 180-day eligibility window expires; automated reverification succeeds or routes only genuine exceptions for human review.
3. A recommended grant proceeds through review, approval, payment, check delivery, return and reversal with consistent balances and audit history.
4. Provider-specific labels, fees, branding, affirmations and eligibility rules differ across isolated tenants.
5. Contributions and investment activity reconcile to balances, AUM and monthly statements.
6. Statements are generated, hashed, retained and retrieved using the simulated clock without violating retention rules.
7. Email, SMS, IVR and webhook integrations simulate successful and failed delivery, retries and duplicate events.
8. A payment provider times out after accepting an operation; retry does not duplicate payment.
9. The UI shows loading, empty, failure, retry and successful states and is testable with Playwright.
10. Hybrid-mode scenarios cannot accidentally write to production systems from simulated workflows.

## Architectural constraints and tradeoffs
- Reuse actual application domain rules; virtualize external boundaries rather than building a parallel business implementation.
- Start with lightweight stateless response mocks, escalating to state machines for money, timing, workflow and compliance.
- Use contract testing with real providers to detect simulation drift.
- Fail closed on environment/tenant ambiguity and block unsafe production writes.
- Keep all core libraries F#-first and WASM-compatible where appropriate; avoid introducing infrastructure-heavy orchestration dependencies.
- Separate simulation artifacts and test evidence from regulated production records.

## Implementation sequence
P0: Contract model, adapter router, deterministic fixtures, isolation and safety controls.
P1: Stateful financial models, virtual clock, orchestration, snapshots and invariant tests.
P2: Forma Scenario Studio, Dokimos integration, visual and agent-driven tests.
P3: Sanitized recording/replay, drift detection, advanced fault injection and provider certification.

## Review questions
- Which contracts are already owned by Strata versus other Echelon components?
- Which business invariants should be owned by DAF domain services instead of Strata?
- How will simulated clocks and event ordering behave across multiple services?
- What minimal real-provider verification is required before each production release?
- How will we measure mock coverage and fidelity without rewarding superficial tests?

Decision: Establish these as proposed requirements, subject to repository architecture review and implementation validation. No code implementation is implied by this document.
