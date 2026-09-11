# Feature Manifest — Semantic model (Tier 1)

Routes to authority. Does not restate rules.

## Purpose

Represents what Strata can know about a database: object identity, the evidence
behind a derived fact, how certain a reference's resolution is, the schema
objects themselves, and the scope bounding every claim.

## Ownership

- State, including presentation state: `Identity.fs` (`Identifier`,
  `QualifiedName`, `ObjectKind`, `ManagementScope`), `Evidence.fs`
  (`EvidenceSource`, `Certainty`, `Fact<'T>`), `Resolution.fs`
  (`Resolution<'T>`, `ResolutionGap`), `Schema.fs` (`Table`, `View`, `Routine`,
  `SchemaSnapshot`), `AnalysisScope.fs` (`Completeness`, `Scope`).
  No presentation state — this tier has no UI.
- Transitions / commands / messages: none — Tier 1 holds no transitions.
  Derivation lives in Tier 2.
- Invariants and guards: `Resolution.isDependencyEdgeSafe` (only `Resolved`
  may become a dependency edge, `DF-STRATA-2026-9B2E`);
  `Fact.create` (a fact cannot exist without evidence, `ER-007`);
  `Scope.supportsAbsenceClaim` (an absence claim requires live inspection,
  complete schema metadata, and a non-empty corpus, `PR-021`).
- Capabilities / authority: not applicable — no capability model yet.
- Important effects and effect contracts: none — Tier 1 performs no effects.

## Interfaces

- Inbound: the public types above, consumed by `Strata.Analysis` and by Tier 4
  adapters.
- Outbound: none. This tier depends on nothing.

## Tests and verification

- Local behavior tests: `tests/Strata.Tests/AnalysisScopeTests.fs`,
  and the `Identifier` folding case in `tests/Strata.Tests/ScopeResolutionTests.fs`.
- Boundary/contract tests: none yet — no wire contract exists (WI-0006).
  `Resolution.tag` is the intended wire vocabulary and is currently untested.
- Integration/live verification: not applicable — no effects.

## Dependencies

- Allowed direct dependencies: `FSharp.Core` only. Enforced by
  `scripts/check-semantic-architecture.sh`.
- Required composition context: none.

## Modification boundaries

- Normal: `src/Strata.Semantic/**`
- Escalation required: adding any `PackageReference` or `ProjectReference` to
  `Strata.Semantic.fsproj` — that contradicts `DF-STRATA-2026-D3F8` and
  `ER-014`, and the architecture check will fail.

## Local agent instructions

- none — repository-level `AGENTS.md` governs.

## Maintenance

- Owner: Strata
- Last checked against implementation: 2026-09-11
- Known gaps: `Schema.fs` models a small subset of notebook §6's introspection
  list; no catalog introspection exists yet to populate it (WI-0005). No
  serialization (WI-0006), so `NFR-001` determinism is unverified.
