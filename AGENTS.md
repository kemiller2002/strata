---
id: GV-START-001
title: Agent Startup Guide
status: canonical
version: 1.3.0
owners:
  - repository-governance
created: 2026-07-22
updated: 2026-09-11
review_cycle: quarterly
supersedes: []
superseded_by: []
related_documents:
  - docs/00-governance/README.md
  - docs/development-telemetry.md
tags: [governance, agents, startup]
---

# Agent Startup Guide

## Mission

The Repository Operating System (ROS) makes research, engineering, decisions, and handoffs durable without relying on conversation history or tribal knowledge.

## Start Here

1. Read [the governance index](docs/00-governance/README.md).
2. Identify the task's scope and operating mode.
3. Locate the applicable canonical domain records; inspect the repository and user changes before editing.
4. State or record material unknowns, constraints, assumptions, and risks.
5. Use the smallest process that preserves correctness, traceability, and continuity.
6. Execute, validate, update affected records, and leave a handoff.

Detailed rules are in the [Agent Operating Manual](docs/00-governance/Agent-Operating-Manual.md). Research packages follow the [REP Specification](docs/00-governance/Research-Execution-Package-Specification.md); engineering follows the [Engineering Standards](docs/00-governance/Engineering-Standards.md).

## Authority

Apply, in descending order: explicit user instruction; applicable safety, legal, and platform constraints; canonical governance; accepted domain REPs and theory; accepted architecture and decision records; current implementation; local convention; agent preference. A higher authority cannot authorize a violation of an applicable safety or legal constraint. When same-level sources conflict, prefer the narrower and newer accepted record and document the resolution; escalate if the outcome materially changes the authorized goal.

## Core Rules

- Never fabricate evidence, file reads, approvals, commands, test results, or certainty.
- Preserve user work. Inspect before modifying; do not destroy or irreversibly migrate without authorization.
- Make reasonable, reversible, in-scope decisions. Escalate high-impact irreversible, security/privacy-sensitive, legally ambiguous, or materially out-of-scope decisions.
- Research by testing hypotheses against confirming and falsifying evidence. Engineering by establishing a baseline, defining acceptance criteria, making the smallest robust change, and testing in proportion to risk.
- Important claims cite `EV-`, `HY-`, and `TH-` records when those records exist. Material decisions use `DF-`, which canonically means **Decision Record**.
- Do not silently change canonical policy. Propose or record the change, its evidence, consequences, version, and migration path.
- Do not claim a test passed unless it ran and passed. Name skipped or unavailable checks and their implications.
- Treat execution telemetry as evidence: discover capabilities, distinguish zero from unavailable, preserve normalized and sanitized raw provider data, prefer deterministic collection, and never invent a metric.
- Not every edit needs a REP. Use the artifact threshold in the Agent Operating Manual.

## Handoff

For substantial work, record: objective; work completed; files changed; decisions and assumptions; tests run and results; evidence added; unresolved questions; risks; and next recommended action. A capable successor must be able to continue without the originating conversation.

## Work Protocol

Before meaningful mutation, identify the external work item and run `./ros work begin --id ID --occurred-at TIMESTAMP` (see the F# CLI note below for the timestamp — it must be the real current time, not an arbitrary one). That transition starts an execution-telemetry record; inspect `./ros work context ID`, classify the work, and ingest runtime telemetry that the current environment can expose. Preserve unknown provider fields through the sanitized raw layer and record unsupported/unavailable capability explicitly. Perform the bounded work, gather configured evidence, request a legal transition with `./ros work complete --id ID --occurred-at TIMESTAMP --evidence TYPE=PATH` (repeatable; finalizes active telemetry), then run `./ros registry build` and `./ros validate`. Use `./ros work block --id ID --occurred-at TIMESTAMP --reason TEXT` and `./ros work resume --id ID --occurred-at TIMESTAMP` rather than hand-editing context. Use `./ros status` when resuming unfamiliar work. Meaningful committed changes require machine-readable attribution; see `docs/work-protocol.md` and `docs/development-telemetry.md`.

No externally-assigned ID yet? Check `./ros work ready` for capturable, unblocked repository work before assuming none exists, and use `./ros add "..."` to record a newly discovered obligation instead of leaving it as an unfiled comment or dropped observation (`add` does not require `--occurred-at`; it defaults to the real current time). `./ros work start --id ID --occurred-at TIMESTAMP` (`begin` is also accepted) promotes a ready backlog item into the protocol above. This local backlog is repository-scoped triage, not a project-management system; see the "Local backlog" section of `docs/work-protocol.md`.

## F# CLI

`./ros` in this source checkout, and every project bootstrapped via `npx
ros-bootstrap init` (both profiles), runs the F# CLI (`DF-ROS-2026-A030`,
`DF-ROS-2026-A032`). Node is no longer a CLI anywhere in this project or
what it scaffolds. Node's own implementation (`tools/ros_cli.mjs` and its
companions) remains in this repository and in the `project-administration`
starter profile only, as `tools/ros_server.mjs`'s/`ros_hub_cli.mjs`'s
in-process internal library dependency (`DF-ROS-2026-A033`) — it is no
longer characterized or scaffolded as a CLI rollback path, and the
`greenfield` starter profile no longer includes it at all. If `./ros`
reports it needs building, run `npm run build:fsharp` first; CI always
builds it before `./ros` runs, so this only affects local/manual use after
a source change.

F#'s command syntax differs from Node's in ways worth knowing rather than
guessing from memory:

- Every mutating command shown above except `add` requires an explicit
  `--id ID` (repeatable) and `--occurred-at TIMESTAMP`, rather than a
  positional ID with an implicit clock read. **Pass the real current
  time** (e.g. `` `date -u +%Y-%m-%dT%H:%M:%S.000Z` ``), not an arbitrary
  or backdated one: a telemetry execution's own `startedAt` always reads
  the real wall clock (matching production), and a later transition whose
  supplied `--occurred-at` predates it fails `./ros validate` with a
  spurious "capability state recording order must be chronological"
  finding — a real trap this decision's own preparation hit and diagnosed,
  not a defect to work around.
- `work start` and `work begin` are both accepted, as are `work complete`
  and `work done`.
- `work context ID` and `work show ID` keep Node's positional-ID form
  unchanged.
- `docs/migrations/fsharp/STATUS.md` is the authoritative ledger of any
  remaining command-surface gaps (e.g. `telemetry finalize --input`, a
  deliberately unported adapter-ingestion-at-finalize path).

This section's command-syntax notes apply equally to `./ros` in this
source checkout and to any project's own bootstrapped `./ros`, since both
run the same F# CLI.

<!-- BEGIN echelon:visual-engineering -->
## Visual Engineering UI research

Managed by `npx @echelon-foundry/visual-engineering`. Do not edit inside this block.

Before designing, implementing, or reviewing UI:

1. Run `npx @echelon-foundry/visual-engineering verify` and stop if it reports a failure.
2. Read `.visual-engineering/AGENT-INSTRUCTIONS.md`.
3. Read `.visual-engineering/UI-FOUNDATIONS.md`.
4. Read `.visual-engineering/UI-DECISION-CHECKLIST.md`.
5. Read `.visual-engineering/UI-ANTI-PATTERNS.md`.
6. Consult `.visual-engineering/RESEARCH-INDEX.md` for provenance and deeper evidence.
7. Inspect the product and its existing design system.
8. Apply the research as decision criteria, not as a visual style.
9. Report the context version, source commit, principles applied, verification
   performed, and justified deviations.

Do not copy Visual Engineering research into this repository by hand.
<!-- END echelon:visual-engineering -->

<!-- echelon:communication-engineering:start -->
## Communication Engineering

Communication Engineering is installed as evidence-bounded operational guidance.
Before producing consequential communication, read:

- `.communication-engineering/COMMUNICATION-FOUNDATIONS.md`
- `.communication-engineering/COMMUNICATION-DECISION-CHECKLIST.md`
- `.communication-engineering/PURPOSE-OUTCOME-MATRIX.md`
- `.communication-engineering/COMMUNICATION-ANTI-PATTERNS.md`
- `.communication-engineering/RESEARCH-STATUS.md`

Treat research maturity as a constraint. Do not turn provisional findings into universal rules, optimize persuasion at the expense of user autonomy, or substitute style for proof obligations.
<!-- echelon:communication-engineering:end -->
