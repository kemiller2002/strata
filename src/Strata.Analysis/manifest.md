# Feature Manifest — SQL analysis (Tier 2)

Routes to authority. Does not restate rules.

## Purpose

Turns a dialect-neutral parse result into resolved references. Owns the
decision of whether a name in SQL refers to a database object at all — the
lexical scope resolution required by `DF-STRATA-2026-9B2E`.

## Ownership

- State: `StatementReferences.fs` (`RelationMention`, `ColumnMention`,
  `StatementShape`, `StatementExtraction` — the adapter contract);
  `ScopeResolution.fs` (`LocalBinding`, `ScopeChain`, `RelationOutcome`).
- Transitions / derivations: `ScopeResolution.resolveRelations`,
  `ScopeResolution.resolveColumns`,
  `ScopeResolution.dependencyEdgeCandidates`.
- Invariants and guards: `dependencyEdgeCandidates` is the single gate through
  which a dependency edge may be created. A CTE name, an alias, a temporary
  relation, and any non-`Resolved` reference are excluded
  (`RK-001`). `resolveColumns` returns `WildcardNotExpanded` for `SELECT *`
  rather than an empty column set (`RK-002`).
- Capabilities / authority: not applicable.
- Important effects: none — this tier is pure. Parsing is a Tier 4 effect.

## Interfaces

- Inbound: `StatementExtraction`, produced by any dialect adapter.
- Outbound: `RelationOutcome` and `Resolution<QualifiedName * Identifier>`
  for columns. Graph construction (WI-0012) will consume these.

## Tests and verification

- Local behavior tests: `tests/Strata.Tests/ScopeResolutionTests.fs`
- Boundary/contract tests: `tests/Strata.Tests/ParserAdapterTests.fs` — drives
  real SQL through the real parser into this tier, proving the adapter's role
  assignment reaches the resolver.
- Integration/live verification: not applicable yet — no live database. The
  `search_path` behaviour here is **unverified against a real server**
  (WI-0014, `EX-STRATA-2026-B2E8`).

## Dependencies

- Allowed direct dependencies: `Strata.Semantic` only.
- Required composition context: none.

## Modification boundaries

- Normal: `src/Strata.Analysis/**`
- Escalation required: changing `StatementExtraction`, which is the contract
  every dialect adapter implements; and any change that would let a
  non-`Resolved` reference reach `dependencyEdgeCandidates`, which contradicts
  `DF-STRATA-2026-9B2E`.

## Local agent instructions

- none — repository-level `AGENTS.md` governs.

## Maintenance

- Owner: Strata
- Last checked against implementation: 2026-09-11
- Known gaps: `QueryLevel` is populated as 0 by the PostgreSQL adapter for
  every mention, so nested-scope shadowing is not yet distinguished from
  top-level shadowing. Correlated subqueries and nested CTEs are therefore
  resolved more coarsely than the type allows. Tracked as a known limitation of
  WI-0008, not as a completed capability.
