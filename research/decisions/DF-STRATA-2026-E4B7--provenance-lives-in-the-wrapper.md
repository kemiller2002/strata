---
id: DF-STRATA-2026-E4B7
title: Who compiled, signed or approved an artifact is recorded in its wrapper, never in the signed body
status: accepted
decision_type: product-architecture
created: 2026-09-26
updated: 2026-09-26
confidence: high
supersedes: []
superseded_by: []
evidence: []
related_documents:
  - DF-STRATA-2026-2F6B
  - DF-STRATA-2026-9A41
  - DF-STRATA-2026-D3F8
  - docs/strata/REQUIREMENTS-ANALYSIS.md
tags: [strata, artifact, provenance, attestation, approval, praxis, echelon]
provenance:
  contributions:
    EXE-20260926T205822698Z-e7fdd3b2:
      operations: [created]
      at: 2026-09-26T21:14:35.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Praxis provenance interchange adoption (WI-0104)"
---

# Decision Record

## Decision

A compiled artifact's file MAY carry a `provenance` member in its wrapper, beside
`integrity` and `signature` and outside `artifact`. Its value is a Praxis
provenance interchange record (`praxis.provenance-record` 1.x; Praxis
`RQ-ROS-2026-A013`), about the subject `strata:artifact/sha256:<body digest>`.

| Command | Operation recorded | Actor | Keyed by |
| --- | --- | --- | --- |
| `compile` | `created` (the ARTIFACT, not the SQL) | the compiling actor | `ROS_EXECUTION_ID` when Praxis propagated one, else `EXE-strata.<run>` |
| `sign` | `x-signed`, evidence `strata:key/sha256:<public key digest>` | the signing actor | same rule |
| `deploy`/`apply --approve` | `approved`, emitted in the run's output (with `--json`, an `{"approval": …}` line) | the approving actor | same rule |

The rules that make it safe:

1. **Outside the signed body.** The digest and the signature cover
   `Artifact.toText` only. Adding, extending or carrying provenance changes
   neither the body's bytes, nor its digest, nor whether a signature verifies.
   Provenance is **self-reported**; the signature is the **custody evidence**.
   Neither is the other.
2. **Authorship is not inferred.** The compiling actor is the creator of the
   artifact, never the author of the SQL in it. The project is named as lineage
   (`derivedFrom: strata:project/<name>@sha256:<digest of the files compiled>`).
   A provenance record for the source change is carried verbatim under
   `sources` only when someone supplies one (`--source-provenance`). Git
   authorship, file owners and comments are never read.
3. **Lossless.** The wrapper keeps the record exactly as read and writes
   through it; unknown fields on the record, a contribution or an actor
   survive; an unsupported major version is carried verbatim and never
   extended; a malformed record is refused rather than dropped; a record whose
   subject is another artifact's digest is refused; unknown wrapper members are
   carried through `sign`.
4. **Nothing invented.** A legacy artifact reads with no provenance. `sign` or
   an approval on such an artifact starts a record holding only its own
   contribution — no `created` is backfilled.
5. **Identity is resolved, never guessed.** Explicit flags (`--actor-kind`,
   `--actor`, `--provider`, `--model`, `--runtime`, `--execution`) win over the
   whitelisted, non-secret environment (`ROS_ACTOR_KIND`, `ROS_ACTOR`,
   `ROS_TELEMETRY_PROVIDER/_MODEL/_RUNTIME`, `ROS_EXECUTION_ID`); a known agent
   runtime's session variable implies an agent (its value is not recorded);
   GitHub Actions with nothing declared is `automation`
   `github/github-actions`; anything else is `unknown`.
6. **Approval.** The approving actor is always reported. An `agent`,
   `unknown` or other non-human approver is reported as a `WARNING`.
   `--require-human-approval` is a policy the operator opts into: it refuses an
   approval whose actor is not a declared human. It is documented as a policy
   over a self-reported identity — identity is not authorization.

## Context

`compile` wrote a deterministic, digest-bearing, optionally signed artifact and
recorded no compiling actor; `sign` recorded no signer or key; `deploy
--approve` printed "APPROVED by --approve" with nobody named, while the help
text said it recorded "that a human accepted". `WI-0089` worried that an agent
can pass `--approve`. Agents now generate and change schema through Strata, and
the Echelon systems exchange provenance as the Praxis interchange record
(`RQ-ROS-2026-A001`, `A004`, `A010`, `A013`–`A015`; Praxis
`DF-ROS-2026-A037`).

## Rationale

- **Why the wrapper.** The body is what is deployed and what is signed; its
  byte-stability is `NFR-001` and the precondition for a signature. A timestamp
  and a run id inside it would make two compiles of one project differ and
  would make a signature attest to who-ran-it, which it cannot. The wrapper is
  already where claims ABOUT the artifact live.
- **Why `x-signed` and not `reviewed`.** Signing proves custody of a key, not
  that anyone read the artifact. `reviewed` would claim a judgement nobody made;
  `approved` is the deployment decision. An `x-` operation is the contract's
  extension point for exactly this.
- **Why record only when identified.** A compile with nothing declared and
  nothing detected would record `unknown` plus a clock, and would cost the file
  its byte-for-byte reproducibility for no information. `--no-provenance`
  suppresses recording entirely; `sign` still extends a record that exists.
- **Why a policy flag and not a default refusal.** Strata cannot authenticate an
  approver. Refusing agents by default would be a control that an agent defeats
  by setting one variable, and would teach operators to set it. As an opt-in
  policy it stops an honest agent (or an unidentified caller) and is described
  as no more than that; custody of deployment credentials remains the control.

## Consequences

- A compile under an identified actor is not byte-reproducible as a FILE; its
  body still is. The CLI determinism test clears the identity environment.
- Tier 1 gains `Strata.Semantic.Provenance` (FSharp.Core only), Tier 3
  `Attribution`, and the host `ProvenanceJson` codec and wrapper handling.
- The codec is tested against Praxis's vendored conformance fixtures
  (`tests/Strata.Tests/fixtures/praxis-provenance-record/`, digests in
  `SOURCE.json`). No project or package reference to Praxis exists.
- `EvidenceSource.ManualDeclaration`'s `declaredBy` is an operator's
  declaration about a DATABASE fact and stays free text; it is not actor
  provenance and is not converted to it.

## Alternatives considered

- **Provenance inside the body.** Rejected: breaks `NFR-001` and entangles the
  signature with who ran the build.
- **A sidecar file.** Rejected: separates the record from the artifact it
  describes, so copying one without the other silently loses it.
- **Writing the approval back into the artifact on deploy.** Rejected: deploy
  never writes the artifact it deploys; the same bytes go to staging and to
  production (`DF-STRATA-2026-2F6B`). The approval record is emitted in the
  run's output for the pipeline to keep.
