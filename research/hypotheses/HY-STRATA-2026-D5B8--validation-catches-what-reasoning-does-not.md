---
id: HY-STRATA-2026-D5B8
title: Deterministic validation against the catalog catches agent SQL errors that reading and reasoning do not
status: proposed
confidence: medium
created: 2026-09-11
updated: 2026-09-11
created_by_agent: claude
research_area: strata-agent-leverage
tests: []
supersedes: []
superseded_by: []
tags: [strata, validation, agent-leverage, hypothesis]
---

# Hypothesis

## Statement

An agent authoring SQL — a query, a view, a stored procedure — produces
statements that reference objects and columns which do not exist, or which
exist with a different type or nullability than the statement assumes.
**Deterministic validation against a catalog snapshot catches a materially
higher share of these than the agent catches by reading the schema and
reasoning**, because the failure is a misremembered or invented identifier
rather than a reasoning error, and rereading does not reliably surface it.

## Falsification criterion

Agents authoring SQL against a schema they can read produce few enough
unresolvable references, or catch enough of their own, that a validator's
marginal yield does not justify it. Concretely: if validation flags nothing an
agent's own review would not have flagged, the hypothesis is false.

## Why this is NOT the rejected hypothesis

`HY-STRATA-2026-2A6F` — "semantic retrieval improves agent correctness" — was
**rejected** after four regimes and thirteen runs. It is important not to
smuggle it back in.

The rejected claim was about **retrieval**: does handing an agent Strata's
answer to "what reads this column" make the agent more correct? It failed
because the hard part of those questions was reasoning, and the agent already
had it — grep plus reading beat or matched Strata everywhere.

This claim is about **validation**, which is a different operation with a
different failure mode:

| | rejected `2A6F` | this hypothesis |
|---|---|---|
| operation | retrieve facts, hand to agent | check a statement, return pass/fail |
| the agent's task | answer a question | author a statement |
| what it competes with | grep plus reading | the agent's own recall |
| failure it targets | incomplete search | invented or misremembered identifier |
| output | context | a verdict with a position |

An agent can read `sales.orders` in full and still write `custmer_id` three
hundred lines later. Reasoning does not catch that; a symbol table does. This
is the same argument that makes a compiler useful to a programmer who
understands their own program.

## Prior art that makes it plausible, and that bounds the claim

`EV-STRATA-2026-C2F5` found two existing implementations, which is evidence the
capability is real and evidence the novelty is limited:

- **DacFx/SSDT** — a stored procedure referencing a dropped column is build
  error SQL71501. This is exactly the proposed capability, for SQL Server, and
  it has existed for over a decade. Its being unremarkable there is mild
  evidence the capability is genuinely useful.
- **`sqlc verify`** — checks committed application queries against a proposed
  schema change.

Neither exists for PostgreSQL as a general validator an agent can call on
arbitrary SQL. That is the gap.

## What would test it

Not a self-authored matrix — that is the mistake `HY-STRATA-2026-6F14` is
still marked `proposed` for.

1. Have agents author SQL of real complexity against a real schema, without
   running it.
2. Validate every statement, and independently establish ground truth by
   executing against the live database.
3. Measure: what share of statements had an unresolvable reference; what share
   the validator caught; what share the agent caught unaided when asked to
   review its own output.

The third arm is the one that matters. Without it the result restates that a
validator validates.

## Risk to the claim

If agents rarely produce unresolvable references when the schema is in context,
the validator is correct and near-useless — and `EV-STRATA-2026-D8E1` already
found agents more capable than expected in an adjacent task. The measurement
must be able to return "the error rate is too low to matter", and that outcome
must be reportable rather than explained away.
