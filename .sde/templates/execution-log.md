---
work_item: WI-XXXX or external ID
change_classes: [Semantic] # one or more; classify consequential sites below
semantic_feature:
feature_manifest:
expected_modification_boundary:
agent:
provider:
model:
runtime_or_session:
telemetry_authority:
started: YYYY-MM-DDTHH:MM:SSZ
---

# Execution log — <short title>

One numbered entry per discrete step, appended as it happens (not
reconstructed afterward — see `docs/00-governance/Agent-Operating-Manual.md`'s
Tool Honesty rule and HelixNote's own `METRICS-LOG.md` convention, which
this template reuses).

1. <what happened> — surfaced by: compiler | architecture-check |
   contract-check | test | search | runtime | self-derived
2. ...

## Telemetry capability baseline

At work start, record the available source and capability state. Reuse the
project's existing ROS/project/harness collector; do not create a parallel
store merely for SDE. Distinguish zero from unsupported, unavailable, or not
captured.

| Metric family | Source | Capability state / completeness | Unit and scope |
|---|---|---|---|
| Agent/provider/model/runtime | | | |
| Input/output/cache-read/cache-write tokens | | | |
| Cost and elapsed time | | | |
| Tool/search/file-read events | | | |
| Build/test attempts and failures | | | |

## Context-discovery log

Record only observable activity. Classify reads/edits against the boundary
declared at task start; do not retrofit categories after seeing the result.

| Event | Path/query | Category (declared feature / declared dependency / undeclared dependency / unrelated / cross-boundary edit) | Why needed / finding |
|---|---|---|---|
| | | | |

## Architecture findings

- Undeclared dependencies:
- Insufficient contracts:
- Manifest corrections:
- Cross-boundary edits and justification:
- Structural concentration or duplicate-authority findings:

## Required-site table (fill in as sites are found)

| Site | Tier | Found by | Mechanically guarded? |
|---|---|---|---|
| | | | |

## Stopping-rule check

- [ ] Acceptance criteria met
- [ ] Applicable checks for this change class (`method/CHANGE-CLASSIFICATION.md`)
      have run and passed, or failures/skips are explained
- [ ] No known high-severity regression remains
- [ ] Unexpected context expansion and architecture findings are recorded
- [ ] Available metrics are recorded; unavailable metrics remain unavailable
