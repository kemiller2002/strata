---
id: SDE-METHOD-005
title: Engineering Metrics
status: draft
version: 0.2.0
created: 2026-09-02
updated: 2026-09-06
related_documents:
  - reference/GLOSSARY.md
  - method/AGENT-EXECUTION-RULES.md
  - method/NAVIGATION-AND-CONTEXT.md
tags: [method, metrics]
---

# Engineering Metrics

Status: RECOMMENDED measurement guidance. Collect only what the environment
can capture reliably and what answers the work's decision question.

## Priority engineering measures (collect routinely)

- Search operations
- Repair loops
- Semantic decisions
- Boundary decisions
- Manual discoveries
- Required change sites
- Silent semantic failures (found by deliberate no-op-style challenge or by
  production incident)
- Integration failures
- Build/test attempts
- Files inspected before first edit and total
- Files changed and cross-boundary edits
- First trustworthy detection stage (which row of
  `method/VERIFICATION-METHOD.md`'s enforcement map actually caught this
  defect)
- Defects by class: Domain/Product, Semantic, Boundary, Tooling/Build,
  Repository/Automation, Methodology, and Experiment-Harness

## Where reliable orchestration data is available, also capture

- Agent/model/provider identifiers exposed by the runtime
- Start/end timestamps and elapsed duration
- Input, output, cache-read, cache-write, and total tokens
- Cost and tool calls
- Tokens consumed before first edit and before successful verification
- Time to first valid change and verified completion
- Build attempts, failed builds, test attempts, and failed tests

## Telemetry authority and adaptive capture (REQUIRED)

Reuse an adopting project's existing telemetry authority—such as ROS,
orchestrator/harness events, CI, build/test logs, or another established
collector—rather than creating an incompatible parallel metric store. The SDE
distribution defines measurement semantics and templates; it is not itself a
runtime telemetry collector.

At T0, discover and record which requested capabilities are available. Keep
`zero`, `not applicable`, `unsupported`, `supported but unavailable`, and
`not captured` distinct using the host system's vocabulary. A missing value
must never be normalized to zero. For each reported metric preserve its unit,
scope, aggregation, evidence class, and completeness window so that a partial
session count cannot be mistaken for a whole-run total.

Capture additional authoritative provider/runtime fields adaptively when the
environment exposes them, even when this document did not anticipate the
field. Use the established collector's sanctioned extension or sanitized raw
layer when one exists; do not alter a frozen experiment schema after T0 merely
to improve apparent coverage. If no compatible field exists, retain the
tool-native evidence where permitted and record the schema/capability gap.
Never collect prompts, responses, source contents, credentials, or personal
data merely to increase metric coverage.

## Context-acquisition telemetry

For a serious experiment—and for routine work when collection is cheap and
reliable—capture:

- search operations and symbol searches;
- repository-wide searches;
- files inspected before first edit and total;
- declared-boundary files read;
- declared-dependency files read;
- undeclared-dependency files read;
- unrelated files read;
- cross-boundary edits;
- repair loops and manual discoveries; and
- the reason for each unexpected context expansion.

These categories depend on a repository semantic map and feature manifest.
When no declared boundary existed at task start, report Declared Context
Surface, CER, and category counts as missing rather than reconstructing a
boundary after seeing the work.

## Serious-experiment checkpoints

Capture cumulative authoritative measures, then calculate deltas, at:

| Checkpoint | Meaning |
|---|---|
| T0 | experiment start |
| T1 | instrumentation/bootstrap complete |
| T2 | semantic foundation established |
| T3 | first vertical slice complete |
| T4 | implementation complete |
| T5 | verification complete |
| T6 | final completion/evidence recorded |

If a checkpoint is inapplicable, mark it inapplicable with a reason. Do not
silently renumber checkpoints or backfill measurements from agent memory.

## The evidence-class rule (REQUIRED)

> Agent self-report and orchestrator-observed telemetry are different
> evidence classes.

Experiment 3 [EV-HN-2026-0005] demonstrated directly that an agent may not
have access to its own authoritative usage metrics (both agents in that
trial correctly reported "unavailable"), while the orchestrating
infrastructure held authoritative figures neither agent could see. It also
demonstrated that even orchestrator-held telemetry can be an incomplete
figure that reads as complete unless the caveat is carried forward: the
harness figures for Condition B covered only a resumed portion following an
interruption, with ~78% of that condition's own eventual log preceding the
counted window.

**Rule:** never estimate unavailable telemetry. Label every reported figure
with its evidence class:

- `SELF-REPORT` — the executing agent's own account.
- `HARNESS-TELEMETRY (complete)` — orchestrator-observed, covering the
  entire run.
- `HARNESS-TELEMETRY (partial)` — orchestrator-observed, but known to cover
  less than the entire run; state explicitly what is missing and why, and
  never present a partial figure's percentage delta against a complete
  figure as a confirmed comparison.
- `NOT OBSERVABLE` — genuinely unavailable in this environment. Use this
  label; never substitute an estimate.

If a metric was not captured contemporaneously and cannot be reconstructed
from durable evidence, report it as `NOT OBSERVABLE` or `MISSING` as the local
schema requires. This applies especially to requirement-level cost/token use,
repair duration, reasoning-error timing, human intervention, and search
attribution.

## Context diagnostics (research only until validated)

**Context Surface** is a semantic set, not inherently a number. A study must
pre-register a counting unit such as unique files, symbols, modules, or tokens.

```text
CER = Actual Context Surface / Declared Context Surface
DE  = files read outside the declared feature boundary
```

CER near 1 is directionally desirable only when the declared boundary was
complete and the numerator/denominator use the same unit. High CER or DE can
indicate hidden coupling, a missing contract, a false boundary, implicit
dependencies, centralization, misplaced shared behavior, or inadequate tests;
it can also be legitimate for cross-cutting work. Neither metric is a quality
score or conformance gate.

## What counts as a research-only metric

Metrics defined specifically for a controlled A/B trial (e.g., the precise
as-experienced vs. architecture-potential MDR reconciliation Experiment 3
introduced to make two agents' incompatible self-reports comparable) belong
in the experiment's own metrics schema, not in routine per-project
engineering dashboards, unless a project is itself running a controlled
comparison. Routine engineering work should track the "priority engineering
measures" above; it does not need MDR/MaDR/BCA-style research metrics
computed on every change. Context Surface, CER, DE, structural concentration,
and exact LOC-distribution measures are likewise research-specific unless a
project has an operational decision that they directly inform.

## Metrics diagnose; they are not targets to game

Per `framework/REP-SPECIFICATION.md`'s own Quality Metrics guidance: define
the denominator or rubric for any percentage reported, and do not let a
metric override evidence quality. A change that minimizes "search
operations" by skipping verification is not an SDE improvement; it is a
metric being gamed.

Metrics also must not hide failure categories. “Zero product defects” is not
“zero engineering-system failures”; report tooling, automation, methodology,
and harness defects separately.
