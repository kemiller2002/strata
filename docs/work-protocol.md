# ROS Work Protocol 1.0

ROS owns the versioned protocol, legal transitions, repository validation, and adapter contract. A consuming repository owns code and evidence. An external project-management service owns work-item truth, prioritization, and portfolio state. Disagreement is reported; no layer silently overwrites another.

## Local protocol

```bash
./ros work begin FEAT-142 --type feature
./ros work context FEAT-142
./ros telemetry show FEAT-142
./ros work block FEAT-142 --reason "waiting for fixture"
./ros work resume FEAT-142
./ros work complete FEAT-142 \
  --evidence implementation=src/feature.js \
  --evidence tests=tests/feature.test.js
./ros validate
./ros validate --json
./ros status
```

The legal semantic core is `ready -> active -> blocked -> active` and `active -> complete`. Local states may be supplied with `--local-state`; `ros.json` maps repository states to the shared semantic vocabulary. Research completion accepts an independent `--conclusion`, including `inconclusive`.

Beginning work automatically starts a segmented execution record under `.ros/telemetry/executions/`; completing work automatically finalizes all active records. Block/resume transitions preserve interruption intervals. Runtime adapters can ingest token, cost, context, agent, tool, and provider-specific observations without changing the work-state protocol. `./ros validate` checks telemetry structure and finalization alongside work attribution. See [`development-telemetry.md`](development-telemetry.md).

`work context` is the normal agent entry point. It reports current state, legal next actions, and evidence required for completion. `status` combines compact work state with repository validation and recommended next actions. Validation errors include deterministic repair guidance; `validate --json` provides a stable structured result for agents and CI consumers.

`.ros/context/current.json` is local work context. `.ros/events/events.jsonl` contains small immutable, idempotently identified semantic events and durable file attribution. `.ros/telemetry/executions/` contains per-execution observations linked from work context and events. These files do not replace the external work item.

Completion validates configured evidence types and paths before changing state. `./ros validate` rejects meaningful dirty paths when enforcement is enabled and neither active context nor a completed event attributes them. CI is the authoritative enforcement boundary; hooks are optional convenience.

Deterministic housekeeping may use the configured `mechanical` work type. It still requires an explicit work-item identity and event, but the default profile does not require implementation/test evidence for that type.

## Local backlog

Beginning a work item with `work begin` requires an ID to already exist. The
local backlog is a cheap, repository-owned staging area for work that has not
been assigned one yet -- captured ideas, discovered obligations, follow-ups --
with its own small lifecycle: `captured -> ready -> {blocked, abandoned}`.

```bash
./ros add "Investigate state payload growth" --tag wasm,state --priority high
./ros work                       # list the unified backlog + in-flight queue
./ros work ready                 # query: items with no blocker
./ros work ready WI-0001         # mutate: captured/blocked -> ready
./ros work show WI-0001
./ros work start WI-0001         # requires ready; delegates to `begin`
./ros work block WI-0001 --reason "waiting on benchmark"
./ros work done WI-0001 --evidence implementation=... --evidence tests=...
./ros work abandon WI-0002 --reason "no longer relevant"
```

Canonical storage is `.ros/work/queue.json`; `.ros/work/queue.md` is a
generated human-readable projection, and `.ros/work/items/<ID>.md` is an
optional free-form detail file `work show` will include when present.

The backlog is **not** a second work-item authority. `work start` requires
`ready`, then delegates directly to the existing `begin` transition above --
from that point the in-flight record in `.ros/context/current.json` is
authoritative, and `work list`/`work show` always prefer its live state over
the backlog's own status field. `work block`/`work ready` on an ID already
being executed dispatch to the existing in-flight transitions, unchanged.
See [`DF-ROS-2026-A008`](https://github.com/kemiller2002/repository-operating-system/blob/main/research/decisions/DF-ROS-2026-A008--repository-local-work-backlog.md)
for why this stays a staging layer rather than repository-owned work-item
authority (that boundary belongs to the external system; see below). For a
worked, example-heavy walkthrough of every command, see
[`work-backlog-guide.md`](https://github.com/kemiller2002/repository-operating-system/blob/main/docs/work-backlog-guide.md).

## Adapter contract

The stable executable interface is `getWorkItem`, `transitionWorkItem`, and `publishRepositoryEvent`. Protocol 1.0 implements a file-backed adapter for conformance tests:

The normalized, testable contract is defined in [`work-adapter-contract.md`](work-adapter-contract.md). Its initial executable operations are `getWorkItem`, `transitionWorkItem`, and `publishRepositoryEvent`; broad listing is deferred.

```bash
./ros adapter publish --target .ros/mock-project-store/events.jsonl
```

Event IDs make retries idempotent. Successful local publication creates `.ros/publications.json` receipts without mutating immutable events. A write error returns failure and creates no success receipt; ROS never treats failure or an unknown remote outcome as success. Production adapters must add authentication, authorization, repository identity checks, version negotiation, retry policy, and explicit `success|failure|unknown` outcomes.

## Adoption and versioning

Initialize a repository with `ros-bootstrap init`, configure `repository` and `workProtocol` in `ros.json`, and call `./ros validate` in CI. Repositories pin a package/protocol version. Breaking semantic or event-schema changes require a new major protocol version; additive evidence types and local mappings are compatible minor changes.

Deferred: remote reads and transitions, signed events, review/approval transitions, commit graph indexing, global aggregation, telemetry publication/retention, and UI. These belong behind the adapter or in the external project-management system—not in ROS core.
