---
id: DF-STRATA-2026-4C7A
title: Adopt pgsqlparser as Strata's PostgreSQL parser adapter
status: accepted
decision_type: architecture
created: 2026-09-11
updated: 2026-09-11
created_by_agent: claude
confidence: high
supersedes: []
superseded_by: []
evidence: [EV-STRATA-2026-7A31]
tags: [strata, parser, architecture, dialect-adapter]
---

# Decision Record

## Decision

Strata's PostgreSQL parser adapter uses **`pgsqlparser` 1.0.0** (MIT), a .NET
wrapper over libpg_query. `PostgresQuery` 0.1.4 (BSD-3-Clause) is the
documented fallback.

## Context

D-003 and P-001 forbid writing a PostgreSQL parser. D-004 named a
libpg_query-based .NET wrapper as the leading direction "pending a technical
spike". Q-001 asked which wrapper.

## Evidence

`EV-STRATA-2026-7A31`. 34 of 36 representative statements parsed; the two
failures were deliberate negative controls and reported accurate located
errors. PL/pgSQL parsed. Fingerprints stable across differing literals.
Statement splitting correct.

## Rationale

- MIT wrapper licence; libpg_query/PostgreSQL licensing applies to the native
  portion, consistent with §143.
- Returns an explicit `Result<T>` with a structured `Error` (message, cursor
  position, context) rather than raising exceptions. This matches Strata's F#
  requirement for explicit `Result` types and no exception-driven control flow,
  so the Tier 4 adapter stays thin.
- Provides `Fingerprint` (notebook §123) and `SplitWithScanner` (§8) directly,
  removing the need for Strata to write a SQL lexer.
- Bundles native binaries for linux-x64, osx-x64, osx-arm64, win-x64.

## Consequences

- Strata pins **PostgreSQL 17.5** as its parser major. `NFR-004` requires this
  be reported in output; `RK-003` covers divergence from a live database's
  major, and `Q-002`/`Q-026` remain open.
- `Google.Protobuf` enters the dependency graph **at Tier 4 only**. The
  protobuf AST must not reach Tier 1 or Tier 2 (D-005, ER-014); the architecture
  check enforces this.
- Native payloads make packaging a real concern (`NFR-005`). The selected
  package ships no linux-arm64 or musl natives; `PostgresQuery` does, and is
  the fallback if Strata must run there.

## Alternatives considered

| Option | Outcome |
|---|---|
| `PostgresQuery` 0.1.4 | Viable; wider native matrix (arm64, musl, win-arm64). Not selected as primary because its API was not exercised and §143 records untested Windows/macOS builds in its own package description. Retained as fallback. |
| `PgQuery` 0.1.6 | **Rejected.** Despite the name it is an Npgsql-based query builder, not a libpg_query wrapper. |
| Write a parser | Forbidden by P-001 / NG-001. |

## Limitations

NuGet's search index is blocked by this environment's egress policy, so
candidates were limited to those the notebook named plus exact-id probes. A
better wrapper may exist that was not discoverable. Only linux-x64 was executed;
Windows and macOS are unverified.
