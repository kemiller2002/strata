---
id: DF-STRATA-2026-8B60
title: What Strata's compile-time safety claims, and what it does not
status: accepted
decision_type: product
created: 2026-09-12
updated: 2026-09-12
created_by_agent: claude
confidence: medium
supersedes: []
superseded_by: []
evidence: []
tags: [strata, validation, compile-time, scope, er-008]
---

# Decision Record

## Decision

Strata's goal is to make SQL **compile-time safe**. The claim is bounded, and the
boundary is stated rather than implied. Four tiers:

| tier | question | oracle | status |
|---|---|---|---|
| **syntax** | does it parse | none | done; cheap, and rarely where bugs are |
| **references** | does `customer.emial` exist | the artifact | done against a live catalog; **offline once the artifact exists** |
| **types** | is `WHERE total = 'abc'` valid; does that function signature exist | a server, via `PREPARE` | **not built** |
| **effectiveness** | does it return the right rows | — | **out of reach, permanently** |

The product claim is tier 2, not tier 1: *"your query is checked against the
schema before it ships."* Describing it as syntax checking would undersell it
into sounding like a linter.

## Rationale

**Tier 2 is where production breaks.** A renamed column, a dropped table, a
typo'd field, a view that lost a column its readers use. Syntactically valid SQL
is the overwhelming majority of SQL that ships broken.

**Tier 3 is reachable without reimplementing PostgreSQL.** `PREPARE x AS SELECT …`
parses, resolves and plans a statement — catching type errors, ambiguous columns
and bad function signatures — **without executing it**. Inside the rolled-back
transaction `ShadowNormalisation` already uses, it is free and side-effect-free.
This is the same bet that made expression comparison work: ask the server rather
than model its rules.

**Tier 4 is not a gap to close.** No static check can say whether a query returns
the right rows, whether a policy admits what was meant, or whether a `CHECK`
encodes the intended rule. Nor can any of this see through SQL built from strings
at runtime, or through `search_path`, `current_setting`, and which role is
connected.

## Consequences

- **Two validation tiers will coexist and must not disagree silently.** The
  artifact check is offline and fast (editor, pre-commit); the `PREPARE` check
  needs a server (CI). A local pass and a CI failure with no explanation is a
  failure mode this project has already hit twice in one session, in `ros
  validate` and in `plan` versus `compile`.
- **`validate`'s three outcomes are the most important thing in the tool.**
  Valid / provably wrong / **unverifiable**. A compiler that answers "OK" when it
  means "I could not tell" is worse than no compiler, because people stop
  looking. This is why the `ER-008` discipline is a precondition for the
  compile-time claim being believable at all, rather than fussiness.
- Declared project invariants — "every table has a primary key", "no object
  grants to `PUBLIC`" — are compile-time checks of the same kind, about the
  project's rules rather than PostgreSQL's. They belong in the same pass.

## Alternatives considered

| Option | Outcome |
|---|---|
| Claim "syntactically correct" | **Rejected.** Accurate but a serious undersell: tier 2 already works and is where the value is. |
| Reimplement PostgreSQL's type rules in Strata | **Rejected.** Same reason the parser is not hand-written and expressions are normalised by the server. `PREPARE` gets the answer from the only authority. |
