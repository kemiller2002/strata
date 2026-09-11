# Artifact Lifecycle Protocol

**Status:** Canonical  
**Version:** 1.0.0

## Purpose

This protocol defines lifecycle state independently from confidence,
completion, and publication tier.

## Research artifacts

Research artifacts use:

```text
draft -> review -> accepted -> superseded | withdrawn
```

- `draft` and `review` content may be edited.
- `accepted` content is immutable. A substantive correction creates a
  superseding artifact; a metadata-only correction requires an amendment.
- `superseded` and `withdrawn` artifacts remain discoverable.
- A superseding relationship must be reciprocal.

Existing REP records using `canonical` are treated as accepted for
compatibility. `deprecated` maps to superseded. `archived` is a storage state,
not permission to remove a canonical record.

## Missions

Missions use:

```text
proposed -> approved -> active -> blocked | completed | cancelled -> archived
```

A completed mission references its outputs and records verification. A
blocked mission names the blocker and executable next step. Reopening a
completed or cancelled mission requires a new mission or an explicit Decision
Record.

## Theories

Theories use:

```text
candidate -> supported -> established -> challenged -> superseded | rejected
```

Theory status describes maturity. Confidence describes evidential strength;
neither implies the other.

## Enforcement boundary

`ros validate` checks allowed states and relationship integrity. Accepted
content mutation requires a comparison baseline, which the initial validator
does not yet implement; until then, review is procedural and this limitation
must remain visible in the migration report and REP.
