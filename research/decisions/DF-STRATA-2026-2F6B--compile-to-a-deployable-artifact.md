---
id: DF-STRATA-2026-2F6B
title: Strata compiles a project to a deployable artifact, and deployment reads the artifact rather than the source tree
status: accepted
decision_type: product-architecture
created: 2026-09-12
updated: 2026-09-12
created_by_agent: claude
confidence: medium
supersedes: []
superseded_by: []
evidence: []
tags: [strata, compile, artifact, deployment, dacpac, agents]
---

# Decision Record

## Decision

Strata gains a **compile step**. `strata compile` reads the project and emits a
single **artifact**; `strata deploy` reads that artifact and a target database,
and never reads the source tree.

```
strata compile --project ./db --connection <any postgres>   -> schema.strata
strata deploy  --artifact schema.strata --connection prod
strata validate report.sql --artifact schema.strata          # no database
```

This is the DACPAC/SSDT model, which is the lineage this project was framed
against from the start.

## Context

The source files are the system of record and servers are updated to reflect
them. Today `plan` and `apply` each re-read the file tree and re-derive
everything, which means:

- two runs against two environments can silently differ, so "what we tested is
  what we shipped" is not a guarantee Strata can make;
- there is no durable object to review, sign, or archive;
- query validation requires a live catalog, so it cannot run in an editor, a
  pre-commit hook, or on a machine without database credentials.

## Rationale

### What compiles, and what cannot

The split is not a matter of taste; one half genuinely cannot be computed ahead
of time.

| | compilable | why |
|---|---|---|
| source -> desired state | **yes** | depends on the source plus *a* PostgreSQL, not the target |
| desired state -> change list | **no** | depends on the target at the moment of deployment |

The same desired state produces different DDL against dev, staging, prod
yesterday and prod now. Baking a change list into the artifact would make it
wrong the instant the target drifted — which Strata already knows, since
"fingerprint changed during planning" is one of the five `apply` refusals.

So the artifact carries the **resolved desired state**, not a plan. Like a
compiled assembly: portable, validated, still requiring a runtime to execute
against a specific machine.

### Compile needs a server, and that is not a defect

Expression normalisation (`ShadowNormalisation`) executes declared DDL in a
throwaway schema inside an always-rolled-back transaction and reads it back,
because only PostgreSQL can faithfully render PostgreSQL. Compile therefore
needs *a* PostgreSQL — but any one of the right major version, not the target.
A throwaway instance in CI suffices, and production credentials are not needed
until deploy.

### The artifact is the offline oracle for query validation

This is the largest single gain and the reason to prefer this shape over
"`plan` but saved to a file". Once the schema is a compiled artifact, every
query in the codebase can be checked against it with no database, no
credentials and no network — the way a header or a `.d.ts` lets callers be
checked without linking.

It also inverts validation authority correctly. Under source-as-system-of-record,
a query that validates against the artifact and fails against production means
**production is stale**, not that the query is wrong. Validating against a live
catalog quietly makes the server the oracle, which contradicts the premise.

## Artifact contents

- the resolved desired state, with every expression as the server rendered it;
- the **resolved file list with per-file content hashes**, however the project
  specified them, so two artifacts can be diffed and an artifact verified
  against a tree;
- the completeness state of the compile;
- the **major version of the PostgreSQL that compiled it**;
- the schema set the project declares.

`Wire.fs` already hand-writes deterministic renderings under Boundary
Preservation — fixed key order, sorted collections, and explicitly no
timestamps, GUIDs, machine names or culture-sensitive formatting (`NFR-001`).
That is precisely the property an artifact hash requires, and it exists today
for output. The artifact reuses it rather than taking a serializer dependency.

## Consequences

- **Server-version skew must be refused, not absorbed.** Normalisation renders
  expressions the way *that* server does. An artifact compiled on PG 16 and
  deployed to PG 15 can differ in rendering, producing a phantom difference or,
  worse, a missed one. `deploy` refuses on a major-version mismatch.
- **An incomplete compile must not produce a whole-looking artifact.** Either no
  artifact, or one that carries its incompleteness and suppresses every drop on
  deploy, exactly as incomplete desired state does today. An artifact that looks
  complete and is not is worse than a failed compile.
- **Signing attaches to the artifact.** The artifact is target-independent, so
  one signature covers every environment, while the gate still evaluates at
  deploy time against the real target — which it must, since "revoke SELECT from
  app_user" cannot be pre-approved without knowing whether the target has that
  grant. Authorship and consequence stay separate.
- **Drift detection falls out.** `strata drift --artifact x --connection prod`
  is the same computation framed as monitoring rather than deployment: exit 0 if
  the server matches, non-zero if it does not. It needs no source tree and no
  deploy credentials, so it can run from a monitoring host.
- Normalisation currently happens in the CLI interleaved with diffing. It must
  move ahead of the compile/deploy split so rendered forms land in the artifact.

## Alternatives considered

| Option | Outcome |
|---|---|
| **Strata generates the SQL** from a structured description the agent supplies | **Rejected.** It inverts the principle the codebase rests on: today Strata's model is a *checked reading* of DDL the server validates, so a modelling bug is a false difference caught by a round-trip. As a generator, the same bug writes wrong DDL into the repository with no independent artifact to check it against — and this model has been wrong about roughly one thing per object type (identity columns, column grants, routine argument modifiers, policy expressions). It also imposes a grammar smaller than SQL, making partial indexes, exclusion constraints, generated columns, collations and non-trivial `CHECK` expressions unreachable until a raw-SQL escape hatch reinstates the original problem, now with two authoring paths. |
| **Sign each source file**, and refuse a file whose signature is absent | **Rejected.** A signature proves provenance, not correctness, and if the agent invokes Strata then the agent can sign. Strata also already walks the whole tree, so an unlisted file is loaded and either modelled (and diffed) or refused — there is no smuggling channel for signatures to close. Git commit SHAs already cover file provenance and the existing fingerprint check already covers time-of-check-to-time-of-use on the database side. |
| **Persist the plan** rather than the desired state | **Rejected.** A plan is target-specific and stale the moment the target moves. Useful as a signed *approval* of one deployment, which is a different object from a compiled artifact. |
