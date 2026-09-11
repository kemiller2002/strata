---
id: SDE-DOCTRINE-004
title: Boundary Preservation
status: accepted
version: 0.2.0
created: 2026-09-02
updated: 2026-09-05
related_documents:
  - architecture/FOUR-TIER-ARCHITECTURE.md
supersedes: []
superseded_by: []
tags: [doctrine, boundary, wire-contract]
---

# Boundary Preservation

Status: `supported` theory (see `TH-SDE-2026-0002`), REQUIRED principles,
RECOMMENDED specific mechanisms (noted per-item below).

## Boundary Preservation is not a fifth tier

Four Tiers ([`FOUR-TIER-ARCHITECTURE.md`](FOUR-TIER-ARCHITECTURE.md)) define
*where semantic responsibility lives*. Boundary Preservation defines *what
happens to semantic information when it crosses between those
responsibilities, or leaves the system*.

## Principles (REQUIRED)

**Preserve closure.** Closed semantic alternatives should remain closed
wherever practical as they cross a boundary.

**Delay weakening.** Do not convert rich semantic values into strings,
generic objects, primitives, or ad hoc URLs earlier than necessary.

> The compiler can only protect the semantics you allow it to see.

**Establish authority.** Duplicated semantic facts must have one authority,
a derivation, or a mechanical agreement check. Physical decomposition never
authorizes separate implementations of the same semantic decision.

**Validate re-entry.** External/untyped data must be validated before
becoming trusted semantic state.

Shorthand for the delay-weakening principle:

```
DU              -> string      as late as possible
value object    -> primitive   as late as possible
typed route     -> URL         as late as possible
effect case     -> fetch options as late as possible
domain field    -> JSON property as late as possible
```

## Failure classes (REQUIRED — preserve the distinction)

Evidenced directly by four built-and-executed experiments in
`EV-HN-2026-0002`, which explicitly tested and refuted a single unifying
"Semantic Boundary Collapse" hypothesis before arriving at this two-class
model:

**Representation Collapse.** A closed/constrained representation becomes
weaker/open crossing a boundary. Examples: a closed HTTP operation becomes a
method string plus a body option; a discriminated union becomes an
arbitrary discriminator string; a value object becomes a primitive; a
semantic field becomes an arbitrary JSON field name.

**Uncoordinated Duplication.** The same semantic fact is independently
maintained in two or more places with no mechanism requiring agreement.
Examples: a client route string vs. a server route; independent field-name
vocabularies; a SQL constraint vs. semantic alternatives.

Some defects involve both — the source investigation's own Failure D
(a projection/field-mapping whitelist) was *caused* by uncoordinated
duplication (three independent field tables) but *manifested* as
representation collapse (the specific whitelist that broke was itself
open-typed). Treat these as compounding, not alternative, explanations when
diagnosing a real defect.

## Three Contract Model (REQUIRED)

**Semantic Contract.** Internal truth — Tier 1/2's own types. Preserve
precision aggressively; there is no external audience to placate here.

**Host Contract.** Communication among tightly cooperating internal
components (e.g., a WASM engine and the browser kernel hosting it, or an
API acting as this application's own backend). Preserve precision
aggressively here too:
- effects described by a closed algebra, one constructor per legal
  operation — not a flat record with optional/nullable fields;
- every boundary-crossing value gets an explicit, hand-written tagged-JSON
  rendering function, never a host language's or framework's default
  serializer;
- route/operation agreement checked by an automated test, not discovered
  live;
- field/projection mappings matched over the actual closed domain type,
  exhaustiveness-checked, never over an open string pair with a wildcard.

**Public Integration Contract.** The stable external contract. Keep
interoperable and boring:

> Rich on the inside, boring on the outside.

Candidate normative rule (status: RECOMMENDED, not REQUIRED — the source
investigation itself rated this "plausible but not directly tested," see
`TH-SDE-2026-0002` limitations):

> A State Programming system MUST NOT require external consumers to adopt
> State Programming in order to integrate with it.

Public APIs should express business intent, not generic semantic mutation:
prefer `/request-correction`, `/approve`, `/reject`, `/reconcile` over a
public generic `/transition` that exposes internal semantic machinery
unnecessarily.

## Wire Contract Rule (REQUIRED)

> Wire representations must be deliberately specified and must not depend
> on incidental host-language or framework serialization behavior.

Mechanisms include explicit DTOs, JSON Schema, OpenAPI, Protobuf, generated
contracts, or explicit parsers/encoders — this is RECOMMENDED guidance on
*that a deliberate mechanism must exist*, not a REQUIRED specific mechanism.
Do not turn hand-written DTOs into doctrine; do not invent a custom DSL
merely because one might be elegant. Preferred evolution order, evidenced by
`EV-HN-2026-0002`'s own standards comparison (none of the four observed
failures required inventing a new standard):

```
standards -> conventions/profile -> semantic metadata -> tooling
    -> custom DSL only if evidence demands it
```

## Derive consequences where practical

Detection optimization makes missing propagation visible. Construction
optimization reduces independently maintained representations in the first
place. SDE should seek one semantic decision driving as many consequences as
practical—legality, validation, capabilities, available UI actions,
transition execution, diagnostics, contract assertions, and tests—without
pretending deliberate external representations can always be eliminated.

The approximately four boundary files per semantic decision measured in the
HelixNote experiments remains research evidence for those mutations, not a
universal constant and not a solved problem [`TH-SDE-2026-0004`]. Derivation
is a design objective; code generation is not mandatory doctrine.

## Enforcement-strength caveat (REQUIRED to state, not to resolve)

Enforcement strength for "closed alternative" preservation varies by host
language (F#/Rust/Java: hard compile-time exhaustiveness; TypeScript: real
but only if the exhaustiveness idiom is written; C#: warning-only by
default). This is a real, measured difference, not glossed over. State the
distinction between semantic conformance and enforcement strength explicitly
whenever citing a cross-language claim.
