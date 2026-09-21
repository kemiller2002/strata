# Ordo Observation and Handoff

ROS can observe executable Ordo decisions without becoming part of the application dependency stack.

## Boundary

Ordo remains the authority for semantic state, evidence, capabilities, obligations, legal transitions, scoped coverage, and effect safety. ROS records what happened, attaches later outcome evidence, derives repository-facing current projections, and packages handoff context.

ROS does **not** authorize an application transition, reinterpret a capability, infer complete coverage, convert an unknown external effect into failure, or turn a scoped negative search into global absence.

## Resolution ingestion

```bash
ros ordo ingest --input path/to/ordo-resolution-observation.json
```

The accepted input is `ordo.resolution-observation` schema version 2. Unknown future versions fail closed. The original record is retained under `.ros/ordo/raw/resolutions/`; a normalized, versioned copy is stored separately. `ResolutionId` is the idempotency key. Re-ingesting semantically identical facts is a no-op; reusing an ID for different facts is a conflict.

## Retrospective assessment

```bash
ros ordo assess --input assessment.json
```

Semantic and operational assessment are deliberately separate:

- semantic: `confirmed | incorrect | unresolved | not-assessable`
- operational: `succeeded | neutral | refused-or-dead-end | harmful-or-failed | outcome-unknown | not-assessable`

Every assessment names evidence references, method, assessor, assessment time, recording time, and limitations. Assessment time and recording time are separate so imported historical labels do not pretend to have been observed live. This history is calibration-ready, but ROS does not automate provider/model routing or calibration policy.

## Scoped search observations

```bash
ros ordo observe-search --input search-observation.json
```

A search record names target, scope, method/query, state reference, scoped coverage, coverage evidence, exclusions, errors, outcome, and time. `searched-not-found` is not an assertion of absence. Consumers must preserve the coverage scope and status.

## External-effect observation

```bash
ros ordo observe-effect --input effect-observation.json
```

Effect outcomes are `succeeded | failed | unknown`. Unknown is a first-class state. Reconciliation, retry blocking, and compensation blocking are recorded independently so an observer cannot silently convert uncertainty into a safe retry. A settled reconciliation carries `reconciledAt`, allowing time-to-resolution to be derived from the original effect attempt without inventing a synthetic duration.

## Effective current projection

```bash
ros ordo current \
  --resolution-id RESOLUTION_ID \
  --superseded-resolution PRIOR_RESOLUTION_ID
```

ROS **never infers authority from recency**. The caller supplies the resolution that the application/repository currently treats as authoritative and any explicitly superseded resolution IDs. ROS validates those references against immutable ingested history and builds a deterministic projection. A matching retrospective assessment is selected by `assessedAt`, then `recordedAt`, then `AssessmentId`.

A newer observation is not authoritative merely because it completed later. No history is overwritten to make a record "current."

## Structured handoff

```bash
ros ordo handoff \
  --revision "$(git rev-parse HEAD)" \
  --source "context/CURRENT-STATE.md" \
  --resolution-id RESOLUTION_ID \
  --authority-artifact "context/CURRENT-STATE.md" \
  --authority-artifact "SDE-MAP.md" \
  --historical-decision "DF-OLD" \
  --fact "Publisher input remains canonical Markdown" \
  --unknown "One external source is not yet verified" \
  --obligation "Run semantic projection verification" \
  --verification "ros validate" \
  --next-action "Execute the verified build plan"
```

A handoff is bound to a repository revision and source and carries current authoritative artifact references, relevant historical/superseded decision references, facts, assumptions, unknowns, obligations, completed verification, legal-next-action descriptions, and an explicitly selected resolution/state fingerprint when supplied. Superseded resolution IDs are caller-declared and validated; ROS does not infer them from timestamps.

The phrase “legal next action” is descriptive handoff data. ROS never decides whether an application action is legal; the application/Ordo boundary remains authoritative.

## Storage

Records live under `.ros/ordo/`:

- `raw/resolutions/` — exact ingested Ordo payloads;
- `resolutions/` — normalized resolution observations;
- `assessments/` — retrospective assessments;
- `search-observations/` — scoped search/negative observations;
- `effect-observations/` — external effect observations.

Record files are content-addressed by a hash of their stable identity to avoid path injection and platform-specific filename rules.
