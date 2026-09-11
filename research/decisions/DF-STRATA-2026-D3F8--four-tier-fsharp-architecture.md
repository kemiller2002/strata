---
id: DF-STRATA-2026-D3F8
title: Strata adopts SDE four-tier architecture with a mechanically checked semantic tier
status: accepted
decision_type: architecture
created: 2026-09-11
updated: 2026-09-11
created_by_agent: claude
confidence: medium
supersedes: []
superseded_by: []
evidence: []
tags: [strata, architecture, four-tier, fsharp, sde]
---

# Decision Record

## Decision

Strata is structured in SDE's four tiers. `Strata.Semantic` (Tier 1) targets
`netstandard2.0` and references **only** `FSharp.Core`, verified by an
architecture check that runs in CI and before every work-item completion.

## Context

`.sde/architecture/FOUR-TIER-ARCHITECTURE.md` is REQUIRED as SDE's canonical
layering. Strata's domain is a natural fit: the semantic model answers "what
can be true about a database", while parsing, catalog access and output are
host concerns.

## Rationale

- ER-014 / D-005 require the semantic model to be smaller than the parser AST
  and independent of it. A Tier 1 project that *structurally cannot* reference
  `Google.Protobuf` or `Npgsql` enforces this at compile time rather than by
  review.
- SDE records this exact check (`check-semantic-architecture.sh`) passing in
  every HelixNote trial across three experiments, and records the four-tier
  split as a `supported` theory.
- F#'s exhaustiveness checking gives hard compile-time enforcement of closed
  alternatives, which `.sde/architecture/BOUNDARY-PRESERVATION.md` notes is
  stronger than C# or TypeScript.

## Consequences

- Dialect adapters (PostgreSQL now, SQL Server later) are Tier 4. Adding one
  must not change Tier 1 or Tier 2.
- Effects are requested as data by Tier 2 and executed by Tier 4; introspection
  and parsing never happen inside the domain.
- Unknown external outcomes are first-class Tier 1 states, not swallowed
  exceptions — required by ER-008 and §144.13.

## Limitations

SDE's four-tier doctrine is `supported` but explicitly not validated outside
HelixNote/F#. Strata is the second adopting project and the first non-HelixNote
trial of SDE v0.2, so this decision carries method risk as well as design risk.
Confidence is `medium` for that reason, not because the layering is doubted.
