# Praxis remote protocol (`praxis.remote` 1.3)

This page specifies the typed request/response contract that lets an agent
with **no local .NET or Praxis runtime** ask a trusted executor to run an
explicit Praxis operation.

- **Why:** [`RQ-ROS-2026-A021`](https://github.com/kemiller2002/praxis/blob/v3.7.2/research/requirements/RQ-ROS-2026-A021--remote-execution-first-class-capability.md),
  which adopts issue #90.
- **How it was designed:** [`DF-ROS-2026-A041`](https://github.com/kemiller2002/praxis/blob/v3.7.2/research/decisions/DF-ROS-2026-A041--remote-execution-protocol-and-adapter-architecture.md).
- **Schemas:** [`schemas/praxis-remote-request.schema.json`](../schemas/praxis-remote-request.schema.json)
  and [`schemas/praxis-remote-response.schema.json`](../schemas/praxis-remote-response.schema.json).
- **Typed model:** `Praxis.Domain.Remote` (`src/Praxis.Domain/Remote/Protocol.fs`).
- **JSON contract:** `Praxis.Contracts.Remote.RemoteJson`.
- **Operating it** (installation, permissions, upgrades, troubleshooting):
  [`remote-execution-operations.md`](https://github.com/kemiller2002/praxis/blob/v3.7.2/docs/remote-execution-operations.md).

> **Status.**
>
> - **Implemented:**
>   - the contract (PRAXIS-REMOTE-01);
>   - identity roles (PRAXIS-REMOTE-02);
>   - the executor boundary `praxis remote execute` (PRAXIS-REMOTE-03);
>   - the verified bootstrap (PRAXIS-REMOTE-05);
>   - the GitHub Actions adapter (PRAXIS-REMOTE-06), described under
>     [GitHub Actions adapter](#github-actions-adapter).
> - **Not yet possible to use live.** A live run needs three things that
>   only a maintainer can provide:
>   - a published Praxis release that contains `remote execute` and carries
>     build-provenance attestations;
>   - that release pinned in `.echelon/toolchain.json`;
>   - the workflow present on the default branch, because GitHub only
>     dispatches workflows that exist there.
>
>   The end-to-end proof (PRAXIS-REMOTE-11) waits on those.

## Principles

- **Praxis remains the authority.** An executor maps a typed request onto
  the *same* command implementation the local `praxis` CLI runs. There is no
  second rule set, so remote validation is never weaker than local
  validation.
- **Operations, never a shell.** The operation catalog is an allow-list.
  No operation accepts a command string. Every request value is untrusted
  data.
- **Transport-independent.** Nothing in the protocol names GitHub. GitHub
  Actions is the first transport, not the protocol.
- **Provider-neutral.** The actor vocabulary is the ordinary Praxis one
  (`agent | human | automation | unknown | x-<extension>`), with free-form
  provider, model and runtime strings.

## Request

```json
{
  "protocol": "praxis.remote",
  "protocolVersion": "1.0",
  "requestId": "req-2026-09-28-work-start-0001",
  "operation": "work.start",
  "repository": {
    "ref": "refs/heads/main",
    "expectedSha": "59b4e032818a4c765886e48c117595dc58019d43"
  },
  "actor": {
    "kind": "agent",
    "id": "example/cloud-agent",
    "provider": "example",
    "runtime": "cloud-agent",
    "sessionId": "session-123"
  },
  "execution": { "id": "EXE-20260928T080000000Z-0a1b2c3d" },
  "arguments": { "workItemIds": ["WI-0100"], "type": "task" },
  "requestedAt": "2026-09-28T08:00:00.000Z"
}
```

| Field | Required | Meaning |
|---|---|---|
| `protocol` | yes | Always `praxis.remote`. |
| `protocolVersion` | yes | `MAJOR.MINOR`. |
| `requestId` | yes | Caller-chosen, stable per intent. 8-128 characters of `[A-Za-z0-9._:-]`, starting with a letter or digit. Reuse it on every retry of the same intent. Never reuse it for a different intent. |
| `operation` | yes | One entry from the catalog below. |
| `repository.ref` | mutations | The branch the request targets (`refs/heads/NAME`). |
| `repository.expectedSha` | mutations | The full commit SHA the request was formed against. |
| `actor` | no | The actor the requester **asserts**. It is self-reported and is never inferred from the executor. Absent values are recorded as `unknown`, and a human has no provider, model or runtime. An absent `actor` means unknown. It never means the runner. |
| `execution.id` | no | Continue the caller's *own* execution. The existing no-impersonation cross-check still applies. |
| `arguments` | per operation | Typed arguments. Unknown argument fields are rejected. |
| `requestedAt` | no | When the caller formed the request (asserted). Praxis stamps transition times from the executor's clock. |

**Unknown fields fail safely.** A field that is not listed is rejected at
any level of the request. The one exception is a field whose name starts
with `x-`. Such a field is a tolerated extension: it carries no meaning, is
not validated, and is excluded from the fingerprint.

## Operations

| Operation | Capability | Arguments | Same implementation as |
|---|---|---|---|
| `praxis.describe` | read | none | discovery document (PRAXIS-REMOTE-07) |
| `status` | read | none | `praxis status` |
| `validate` | read | none | `praxis validate --json` |
| `work.context` | read | `workItemId` | `praxis work context ID` |
| `provenance.identity` | read | none | `praxis provenance identity --json` |
| `request.status` | read | `requestId` | request-journal lookup |
| `work.start` | mutate | `workItemIds[]`, `type?`, `classifications[]?` | `praxis work start` |
| `work.resume` | mutate | `workItemIds[]` | `praxis work resume` |
| `work.block` | mutate | `workItemIds[]`, `reason`, `unrecoverableReason?` (1.3) | `praxis work block` |
| `telemetry.record` | mutate | `metric`, `value`, and optional `workItemId`, `unit`, `currency`, `quality`, `confidence`, `scope`, `sourceType`, `sourceName`, `mechanism`, `pricingSource`, `pricingVersion`, `collectedAt` | `praxis telemetry record` |
| `work.complete` | complete | `workItemIds[]`, `evidence[{type, path}]?`, `conclusion?` | `praxis work complete` |
| `work.reconcile` | reconcile | `workItemId`, `reason`, and at least one of `commits[]` or `ranges[]` (`BASE..HEAD`), plus `paths[]?` | `praxis work reconcile` (#80) |
| `step.start` (1.1) | mutate | `stepId`, `name?`; requires `execution.id` | `praxis telemetry step start` |
| `step.complete` (1.1) | mutate | `stepId`, `reason?`; requires `execution.id` | `praxis telemetry step complete` |
| `step.fail` (1.1) | mutate | `stepId`, `reason?`; requires `execution.id` | `praxis telemetry step fail` |
| `batch` (1.2) | each constituent's own | `requests[]` of `{requestId, operation, execution?, arguments?}` | each constituent's command, in order |
| `work.checkpoint` (1.3) | mutate | `workItemId`, `summary`, `nextAction`, `stepId?`; requires `execution.id` | `praxis work checkpoint --json` |
| `work.continue` (1.3) | mutate | `workItemId` | `praxis work continue --json` |

**Version 1.1 additions.** Version 1.1 adds the step operations and the
optional `step` argument of `telemetry.record` (PRAXIS-REMOTE-04). A 1.0
request cannot use them.

**Step operations** act on the requester's *own* execution. That execution
must be named in `execution.id`, and it must be one the requester may
continue.

**Completion finalizes only the requester's own executions (GH-113).**
`work.complete` finalizes every active execution of each item it completes,
so every one of them must be an execution the requester may continue,
whether or not the request names one in `execution.id`. If any belongs to
another actor or run -- including another session of the same agent -- the
request is refused as `domain-rejected` on `arguments.workItemIds`, and
nothing is persisted. The successor takes the work over with `work.continue`
first (which records the predecessor as interrupted), then completes it in
its own execution. The owner of the only active execution may still omit
`execution.id`.

**Batches (version 1.2).** The `batch` operation carries ordered
constituents in `arguments.requests`. Each constituent has the form
`{requestId, operation, execution?, arguments?}`. Batching saves runner
start-ups without weakening any guarantee.

- **Shared context.** Constituents share the batch's protocol version,
  repository binding and actor. Each keeps its own request ID, journal
  entry, fingerprint and outcome. A batch cannot contain a batch.
- **Authorization.** The batch is authorized for the union of its
  constituents' capabilities, and bound to `expectedSha` once, before the
  first constituent runs. Each later constituent runs on the state that the
  constituents before it produced.
- **Replay.**
  - A constituent that is already journalled with the same fingerprint
    replays instead of running again.
  - A constituent whose ID is journalled with a different fingerprint is an
    `idempotency-conflict`.
  - A retry of the whole batch with the same batch ID replays the batch's
    recorded response.
- **Stopping.** The first constituent that does not succeed stops the batch.
  It is undone exactly, back to how it found the tree. Constituents that
  already succeeded stay done.
- **The response is never ambiguous.**
  - `outcome` and `failure` are those of the constituent that stopped the
    batch, and `failure.message` names it.
  - `result` lists `total`, `completed`, `stoppedAt`, `notRun`, and every
    constituent response that ran.
  - `persistence.paths` lists the state that successful constituents kept,
    so the adapter persists that state even when the batch as a whole did
    not succeed.

**Durable checkpoints (version 1.3,
[`DF-ROS-2026-A042`](https://github.com/kemiller2002/praxis/blob/v3.7.2/research/decisions/DF-ROS-2026-A042--durable-work-checkpoints-and-executor-continuation.md)).**

- **`work.checkpoint`** is recorded in the requester's own execution. The
  request never asserts a commit: the executor is checked out at
  `expectedSha`, and the command verifies that commit against the remote
  branch head itself.
- **Persisted state.** The adapter's commit of the resulting Praxis state
  changes only non-meaningful paths, so the checkpoint stays current.
- **`work.continue`** creates the successor's execution, whose parent is the
  predecessor.
- **`unrecoverableReason`** on `work.block` is added in 1.3. Without it, the
  block fingerprint is byte-for-byte the 1.0-1.2 encoding, so journalled
  requests still replay.
- **Discovery.** `praxis.describe` publishes `requiresExecution` and
  `introducedIn` for every operation.
- **Reading state.** `work.context` returns the `continuity` block. A remote
  reader sees the historical checkpoint and its current recoverability
  separately.

The `admin` capability is reserved.

**Capabilities.** The executor grants capabilities from *trusted*
configuration. A request document never grants its own. `read` never
implies any mutation, and `mutate` implies neither `complete` nor
`reconcile`.

### Untrusted-input rules

The protocol checks shape and safety only. Whether a transition is legal,
or a metric is well formed, is still decided by the command that runs.

- **Identifiers** (work items, tokens, actor values) start with a letter or
  digit, so they can never be read as an option.
- **Branch refs:**
  - must match `refs/heads/NAME`;
  - must not contain `..` or `//`;
  - must not end in `/` or `.lock`.
- **Revisions** must be lowercase hexadecimal commit SHAs. Symbolic
  revisions such as `HEAD~1` or branch names are refused remotely.
- **Paths:**
  - must be repository-relative, with `/` separators;
  - must not contain `..`, `\`, `:`, empty segments or control characters;
  - must not start with `/` or `-`.
- **Free text** (`reason`, `conclusion`):
  - 1 to 2000 characters;
  - no control characters except newline and tab;
  - must not start with `-`.
- **Line terminators.** Every pattern is anchored to the true end of the
  value. A trailing newline cannot slip past a pattern, as it could with a
  `$` anchor.
- **Telemetry cannot claim executor observation.**
  - `telemetry.record` refuses the source types that denote Praxis's *own*
    observation: `ros-git`, `ros-clock` and `environment`.
  - Supplied values keep their asserted `quality` and `sourceType`. For
    example, `observed` with `runtime-api` means "provider-reported,
    relayed by the requester".
  - A missing `value` is refused. It never becomes zero.
- **Credential material is refused and never echoed.** A request that
  contains what looks like a secret is refused with `secret-detected`. The
  response names the offending field but never includes the value.
  Patterns cover:
  - GitHub tokens
  - `sk-` API keys
  - AWS access keys
  - Google API keys
  - Slack tokens
  - private-key blocks
  - JWTs

## Versioning

- **Supported versions.** An executor at `1.N` accepts requests at `1.0`
  through `1.N`. A request from a newer minor version, or from a different
  major version, is `unsupported-protocol`, and the diagnostics name the
  supported versions.
- **Version is checked first.** The protocol and its version are read before
  anything else in the request. A newer-version request carrying fields this
  executor does not know is therefore reported as unsupported. It is not
  reported as a list of unknown fields.
- **Minor versions** may only add optional fields or new operations.
  Changing the meaning of a field or operation requires a new major version.
- **History is kept.** Journal entries record the protocol version they were
  written under. They are never rewritten to a newer schema.

## Decision order

The order below is part of the contract.

1. **Well-formed document.** The document must be a JSON object. Otherwise
   the result is `invalid-request`.
2. **Protocol and version.** Otherwise `unsupported-protocol`.
3. **Known operation.** Otherwise `unsupported-operation`.
4. **Structure, values and secret scan.** Otherwise `invalid-request` or
   `secret-detected`.
5. **Mutation binding.** A mutation must name `repository.ref` and
   `repository.expectedSha`. Otherwise `invalid-request`.
6. **Authorization.** The trusted grant must include the operation's
   capability. Otherwise `unauthorized`. This check comes *before* replay,
   so a caller without authority cannot read another request's recorded
   outcome.
7. **Journal** (mutations only). If the same `requestId` is recorded with
   the same fingerprint, the result is a **replay**. The recorded response
   is returned with `replayed: true`, and nothing executes again. If the
   fingerprint differs, the result is `idempotency-conflict`.
8. **Repository binding** (mutations only). The executor's checked-out ref
   and commit must equal `repository.ref` and `repository.expectedSha`.
   Otherwise `stale-ref`.
9. **Execute** the same command implementation as the local CLI.

### Idempotency

The fingerprint is a SHA-256 hash (`sha256:<hex>`) of a length-prefixed
encoding of these fields:

- protocol and major version
- operation
- ref
- actor
- execution
- typed arguments

It deliberately **excludes** `expectedSha`, `requestedAt` and `x-` fields.

Here is why. A caller whose mutation succeeded, but whose result was lost in
transit, will see the branch moved *by its own commit*. When it retries with
the same `requestId`, the journal is consulted before the stale-ref check,
so the caller recovers the original outcome. The retry does not fail as
stale, and the transition is not applied twice. Whether the retry names the
old SHA or the new one makes no difference.

## Response

```json
{
  "protocol": "praxis.remote",
  "protocolVersion": "1.0",
  "requestId": "req-2026-09-28-work-start-0001",
  "operation": "work.start",
  "outcome": "rejected",
  "replayed": false,
  "repository": {
    "ref": "refs/heads/main",
    "expectedSha": "59b4e032818a4c765886e48c117595dc58019d43",
    "observedSha": "8646ade0000000000000000000000000000000aa"
  },
  "praxisVersion": "3.4.0",
  "failure": {
    "code": "stale-ref",
    "decidedBy": "praxis",
    "retry": "after-refresh",
    "message": "the ref has moved since the request was formed; re-read the repository state and form a new request",
    "problems": [
      {
        "field": "repository.expectedSha",
        "message": "does not match the observed commit"
      }
    ]
  },
  "result": null
}
```

`result` carries the executing command's own machine-readable output,
unchanged. A `requestId` that fails validation is never echoed back.

### Outcomes and failures

| `outcome` | Meaning |
|---|---|
| `succeeded` | The operation ran, and for a mutation, its state was persisted. |
| `rejected` | Praxis refused before any effect. |
| `failed` | The attempt ended, and it is known that nothing was persisted. |
| `unknown` | An effect may have been persisted and has not been confirmed. It is never promoted to success or failure. |

| `failure.code` | `decidedBy` | `outcome` | `retry` |
|---|---|---|---|
| `invalid-request`, `secret-detected`, `unsupported-operation`, `unsupported-protocol`, `unauthorized`, `idempotency-conflict`, `domain-rejected` | praxis | rejected | `never` |
| `stale-ref` | praxis | rejected | `after-refresh` |
| `validation-failed` | praxis | failed | `never` |
| `internal` | praxis | failed | `same-request` |
| `concurrency-conflict` | executor | failed | `after-refresh` |
| `bootstrap-failed` | executor | failed | `same-request` |
| `repository-write-failed`, `transport-failed`, `rate-limited`, `timeout`, `cancelled` | executor | unknown | `same-request` |

The retry values mean:

- `never`: change the request first.
- `after-refresh`: re-read the repository state and form a new request.
- `same-request`: retry with the *same* `requestId`. This is safe only
  because mutations are journalled.

After an `unknown` outcome, ask `request.status` or retry with the same
`requestId`. Never mint a new ID for the same intent.

## Identity roles

Each of these is recorded separately. None is ever collapsed into another.

| Role | Source |
|---|---|
| Request actor | Asserted in the request. |
| Executor or runner | Observed by the executor. |
| Transport principal | Authenticated by the transport. |
| Git change author | Git history. |
| Reconciliation actor | The actor who reconciles post-hoc attribution. |
| Work-item attribution | The work item a change is attributed to. |

A runner never becomes the author of an agent's work (PRAXIS-REMOTE-02).

## Executing a request

```
praxis remote execute --request FILE [--grant read|mutate|complete|reconcile]* [--output FILE] [--timeout-seconds N]
```

(`ros remote execute` works the same, for compatibility.) The command prints
the response JSON, writes it to `--output` when given, and exits with one of
these codes:

- `0` when the outcome is `succeeded`.
- `1` for any other outcome.
- `2` for bad command-line arguments.

**Who runs it.** The executor boundary is run by a trusted adapter, for
example a CI job. It is not run by the requester.

**Grants.** `--grant` is the transport's grant, set from trusted adapter
configuration. It is intersected with the repository's own opt-in:

```json
{ "remote": { "capabilities": ["read", "mutate", "complete", "reconcile"] } }
```

This setting lives in `ros.json`. A repository that says nothing allows
remote **reads only**, so adding the executor never grants mutation by
itself.

**How the command runs.** An accepted request runs this same binary's
local command in a child process:

- It passes a typed argument list and never uses a shell.
- It uses the executor's clock for `--occurred-at`.
- It uses a *derived* environment. The requester's asserted actor is set
  explicitly, the executor's observed facts are included, and only an
  operational allow-list of variables survives. Host identity markers and
  credentials are never passed on. See `docs/agent-provenance.md`.

**Around a mutation, the boundary also:**

1. Refuses a working tree with uncommitted changes (`stale-ref`), because
   such a tree is not the commit the request names.
2. Records `validate` findings before and after. A mutation that introduces
   a new validation error is undone and reported as `validation-failed`.
3. Undoes anything written by a refused, failed, or timed-out command, and
   keeps only Praxis-owned state (`.ros/**` outside locks and transactions).
4. Writes the journal entry `.ros/remote/requests/<requestId>.json`. It
   holds the fingerprint, asserted requester, principal, repository binding
   and full response. `:` in a request ID becomes `~` in the file name.
5. Reports every path the adapter may commit in `persistence.paths`,
   including the journal entry. The adapter commits exactly those paths, in
   one commit.

**Concurrency.** Mutations are serialized per working tree, with the journal
lookup, command and journal write held under one lock. Across runners,
`expectedSha` and a non-fast-forward push serialize them. A request that
lost the race fails as `stale-ref` and must be re-formed.

**Finding out what happened.** `request.status` reports whether a request
ID is recorded, and returns the recorded response when it is. A mutation
whose result was lost is recovered by retrying it with the same
`requestId`, or by asking `request.status`. The answer comes from the
repository, never from guessing.

## GitHub Actions adapter

The adapter is made of three files:

- [`.github/workflows/praxis-remote.yml`](https://github.com/kemiller2002/praxis/blob/v3.7.2/.github/workflows/praxis-remote.yml)
- [`.github/actions/praxis-remote`](https://github.com/kemiller2002/praxis/blob/v3.7.2/.github/actions/praxis-remote/action.yml)
- [`.github/actions/praxis-setup`](https://github.com/kemiller2002/praxis/blob/v3.7.2/.github/actions/praxis-setup/action.yml)

Together they form a thin host. They bootstrap the pinned, verified Praxis
release, let Praxis decide, and persist exactly what Praxis reports. They
contain no Praxis domain rules.

**Invoking it.** A caller needs GitHub access and nothing else. It
dispatches the workflow on the branch the request targets, for example with
the GitHub CLI:

```
gh workflow run praxis-remote.yml --ref main \
  -f request_id=req-2026-09-28-work-start-0001 \
  -f request="$(cat request.json)"
```

The REST API works too: `POST /repos/{owner}/{repo}/actions/workflows/praxis-remote.yml/dispatches`.

**Getting the result.** There are three ways, in order of durability:

1. **The request journal.** For a mutation, the entry
   `.ros/remote/requests/<requestId>.json` is committed together with the
   state it describes. Read it through the contents API, or send a
   `request.status` request.
2. **The workflow run.** Its `run-name` is `praxis remote <request_id>`. The
   step summary holds the response and the adapter result.
3. **The workflow artifact.** It is named `praxis-remote-response` and is
   kept for 30 days.

Logs and artifacts are supporting evidence only. The journal is the durable
record.

**Trust boundary.**

- **Who can start it.** Only `workflow_dispatch`, which requires write
  access to the repository, and `workflow_call` can start the workflow.
  There is no `pull_request` trigger, so forks can neither run it nor
  obtain its credentials.
- **The inbox relay** (`praxis-remote-inbox.yml`, `DF-ROS-2026-A045`) is for
  a writer that cannot dispatch. It runs on a push to a `praxis-inbox/**`
  branch that adds or changes `.praxis-inbox/*.json`, and dispatches this
  workflow with each file's exact bytes on the ref the request names.
  - It needs only `contents: read` and `actions: write`.
  - It checks only what routing needs: the document is JSON, the
    `protocol`, the `requestId`, and a `refs/heads/` ref that is not an
    inbox branch.
  - Everything else is decided here, by Praxis.
  - Pushing a branch requires write access, so its trust boundary is the
    same as dispatch's.
- **Credentials per job.** The default is `permissions: {}`.
  - `praxis remote classify` decides whether a request mutates.
  - Reads execute in a job with `contents: read`.
  - Mutations run in a job with `contents: write`. That job is serialized
    per ref, and the repository's `remote.capabilities` narrows it further.
  - `pull-requests: write` is granted only when `persistence: pull-request`
    is chosen.
- **Request handling and secrets.** The request travels through the
  environment into a file and is never interpolated into a shell. No
  secrets are passed.
- **Pinned actions.** Every third-party action is pinned to a commit SHA.

**Persistence.** `scripts/praxis-remote-persist.sh` handles persistence:

1. It commits exactly `persistence.paths`, after re-checking that each path
   is Praxis-owned `.ros/` state and refusing anything else that is staged.
   Praxis lists only state it kept. That includes the successful
   constituents of a batch that stopped part-way, which are persisted even
   though the job reports the batch's failure.
2. It commits as the executor, `github-actions[bot]`, with these trailers:

   ```
   Praxis-Request-Id: <id>
   Praxis-Operation: <operation>
   Praxis-Requester: <kind>:<id> (asserted by the request)
   Praxis-Executor: <kind> run <run> attempt <attempt>
   Praxis-Version: <version>
   ```

   The runner never poses as the agent, and the agent is never recorded as
   the author of bytes the runner wrote.
3. It pushes without force.
   - If the ref moved first, the result is `concurrency-conflict` with
     `after-refresh`, and nothing was persisted.
   - In `pull-request` mode, the state is pushed to
     `praxis/remote/<digest of the request ID>` and a pull request is
     opened. The result is `persisted: false` with the pull request URL,
     until the pull request is merged. A same-request retry reuses that
     branch when its tip carries the request's `Praxis-Request-Id`, and
     reports `reused: true`; it never pushes a second state for the request.

The adapter writes its own `praxis.remote-adapter-result` document next to
the Praxis response. The job succeeds only when the Praxis outcome is
`succeeded` and persistence did not fail.

**Opting in.** Adding the workflow grants nothing on its own. A repository
enables remote mutation by listing capabilities in `ros.json`, under
`remote.capabilities`. Without that setting, remote requests can only read.
