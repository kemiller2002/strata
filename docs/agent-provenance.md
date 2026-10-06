# Agent identity and provenance

Praxis records **who or what produced each meaningful piece of work**: an agent,
a human, or an automated non-agent process. It also records **which run** did it.
Attribution does not come from Git authorship, prose, or inference.

The records live in three places, all extensions of the existing Praxis
architecture:

| Record | Where | What it answers |
|---|---|---|
| **Execution identity** | `.ros/telemetry/executions/EXE-*.json` → `identity` (now with `actorKind`) | Which run, which agent, provider, model, runtime, and session |
| **Action provenance** | `.ros/events/events.jsonl` → `actor` on every work event; `artifact.contributed` events | Who performed a transition or contribution, when, in which execution, and on which state |
| **Artifact provenance** | front matter `provenance.contributions` on canonical artifacts (requirements, decisions, evidence, …) | Original creator, every later contributor, and each contributor's execution, reason, and evidence |

Requirements for this capability: `RQ-ROS-2026-A001` through `RQ-ROS-2026-A012`
(`research/requirements/`). Decision: `DF-ROS-2026-A036`.

## Why

A repository shared by several agents and humans needs to answer these questions
without anyone's memory:

- who created a requirement and who changed it since;
- which execution produced a finding, a decision, or some evidence;
- whether the result came from an agent, a human, or CI;
- whether a human corrected an agent's work, or one agent revised another's.

Metrics over agents need the same records: output, rework, defects,
validation failures, cost and duration by agent, model, or provider. See
[Metrics](#metrics).

## Identity, execution, and actor

Praxis records identity at two levels that it keeps separate.

- **Stable identity: the actor.** This identifies *who*, and it is the same
  across runs. It is a small, portable object:

  ```json
  {"kind":"agent","id":"openai/codex","provider":"openai","model":"gpt-5-codex","runtime":"codex"}
  ```

  - `kind` is `agent`, `human`, `automation` (CI or another deterministic
    non-agent process), or `unknown`. A namespaced `x-...` extension is also
    allowed.
  - `id` is the stable identifier:
    - an explicit `--agent`/`--actor`/`PRAXIS_ACTOR` value always wins;
    - otherwise a non-human actor whose provider and runtime are both known
      is identified as `provider/runtime`;
    - otherwise the id is `unknown`.
  - `provider`, `model`, and `runtime` are present for every non-human actor,
    as the literal `"unknown"` when not known. They are omitted for a human,
    because they do not apply. "Unknown" and "not applicable" are never
    conflated.

- **Execution identity: the run.** This identifies *which run*. It is the
  existing `EXE-<timestamp>-<random>` telemetry execution ID, created by
  `work begin`. Two runs of the same agent always get two execution IDs, and
  everything produced in one run carries that run's ID.

A contribution is keyed by the execution that produced it. Contributions from
two runs of the same agent therefore share an actor `id` but have different
keys. They never merge, and neither is ever mistaken for the other.

### How an agent establishes identity: once per execution

1. **Start the execution.** Run `./praxis work begin --id ID --occurred-at NOW`.

   Identity is discovered from a small whitelist of non-secret environment
   variables, and explicit declarations always win:

   | Source | Examples |
   |---|---|
   | Flags on `work begin`/`resume`/`block`/`complete`, `add`, and `telemetry start` | `--actor-kind`, `--agent`, `--actor`, `--provider`, `--model`, `--runtime` |
   | Environment variables set once per session | `PRAXIS_ACTOR_KIND`, `PRAXIS_ACTOR`, `PRAXIS_TELEMETRY_PROVIDER`, `PRAXIS_TELEMETRY_MODEL`, … |
   | Known agent runtimes | Codex, Claude Code, Gemini CLI, Copilot |
   | Known CI | GitHub Actions resolves as `automation` |

   The legacy `ROS_*` names of these variables still work; the `PRAXIS_*`
   name wins when both are set.

   Nothing is guessed. A local model server (`OLLAMA_HOST`) proves nothing
   about who is acting, so it implies no actor kind.

2. **Check who you will be recorded as.** Run `./praxis provenance identity`. It
   prints the resolved actor, the mechanism that determined it, and the active
   executions.

3. **Work normally.** Work events carry the actor automatically. To attribute an
   artifact you created or materially changed, run:

   ```bash
   ./praxis provenance record --path research/requirements/RQ-...md --operation created \
     [--reason "why"] [--evidence EV-...] [--derived-from ID]
   ```

   The command inherits the actor from the active execution record, so the
   agent does not repeat its identity.

4. **Finish.** `./praxis work complete …` then `./praxis validate`, which catches
   missing or broken provenance.

A recording inherits the execution's identity only when the recording process
is plausibly that same run. Praxis never attributes a contribution to another
actor's execution, or to another run of the same agent. The rules are:

- **The process must have an identity of its own.** The identity can come from
  a flag, from `PRAXIS_ACTOR`/`PRAXIS_ACTOR_KIND`, or from a detected agent runtime.
  A process with no identity at all, such as a plain terminal, never inherits
  an execution implicitly: it must declare itself, or name the execution with
  `--execution`.
- **The identity must agree with the execution's actor.**
- **Session and run IDs must match.** When both sides know a session,
  conversation, or CI run ID, the two must be equal. A second Claude Code
  session, a second Codex thread, or a second CI run is a different run.
- **Implicit inheritance needs positive evidence.** A shared run key, or the
  same known stable actor id, must show the process is that run. Declaring
  only `PRAXIS_ACTOR_KIND=agent` agrees with every agent, so it proves nothing.
- **`--execution` is an explicit assertion.** Naming an execution means "I am
  acting within this run". A declared process must still agree with that
  execution. A process with no identity is taken at its word, so only the
  actor actually running the execution should use it; everyone else should
  declare themselves.
- **An agent must record inside its own execution.**
- **Several active executions.** Praxis picks the one that matches the recording
  process, or asks for `--execution`.
- **Outside any execution.** A declared human or automation acting outside any
  execution (for example, a human approving while an agent is mid-run)
  contributes under a `CTB-<UTC day>-<actor digest>` key. One actor's
  recordings on one day form a single entry that merges operations and
  advances `last`, exactly like an execution's entry.

## Identity is provenance, not authentication

Everything described here is **self-reported provenance**. A record saying
`provider: openai` does not prove that OpenAI produced anything, and
`kind: human, id: kevin` does not authenticate Kevin. Praxis guards against
*accidental* misattribution:

- it resolves identity only from the current process;
- it never inherits another run's actor. The legacy "last actor" field in
  `.ros/context/current.json` is not provenance and is never used for it;
- it cross-checks every contribution against its execution record;
- it refuses re-attribution.

Praxis does **not** defend against a malicious process that lies about itself.

The model leaves room for stronger **attestation** later, without redesign:

- Each contribution is a keyed mapping entry, so a future signer can add
  fields such as `attestation` or `signature`. `provenance record` edits the
  front matter surgically and never re-renders another contributor's entry,
  so unknown fields survive. The test suite covers this.
- The execution record is the natural place for an external execution receipt
  or CI attestation (GitHub Actions OIDC, a GitHub App installation identity, a
  signed run manifest). The actor already distinguishes `automation` from
  `agent`.
- Events carry `previous.sha256` and `current.sha256` content digests, which
  a signature can cover.

Signed events, verified execution receipts, and key management are deferred.
They belong behind the adapter boundary or in an attestation service, not in
Praxis core; see `DF-ROS-2026-A036`.

## Remote execution: requester, executor, and principal

A remote request can come from a cloud agent without a local Praxis
runtime; see [`remote-protocol.md`](remote-protocol.md) and
`DF-ROS-2026-A041`. When it does, the process that runs Praxis is a runner,
for example a GitHub Actions job, acting on the agent's behalf. Three
different facts are recorded, and none of them is ever collapsed into
another:

- **Requester (the actor).** This is the actor the request *asserts*. It
  becomes the execution's `identity` and the actor on its events. The
  execution marks it with `identity.assurance: "asserted-by-request"`.
  - If the request does not name an actor, every field is `unknown`. It is
    never recorded as the runner.
- **Executor.** These are the facts the runner *observed* about itself: its
  kind, run ID and attempt, workflow ref, repository, host, and the Praxis
  version it ran. They are recorded as the execution's `executor` block,
  marked `assurance: "observed-by-executor"`.
  - The runner's run ID belongs to the executor, not to the agent. It is
    never copied into `identity.runId`.
- **Transport principal.** This is the account the transport authenticated,
  such as the GitHub user who triggered the run. It is recorded as
  `executor.principal`. It is who *asked the runner*, which can differ from
  the actor who did the work.

The command runs in a child process whose environment is *derived*, not
inherited (`RemoteIdentity.childEnvironment`):

- Every variable that identity discovery reads is removed, so the runner's
  own markers cannot be read as the agent's identity. These include
  `GITHUB_ACTIONS`, `GITHUB_RUN_ID`, an agent session variable, and a stray
  `PRAXIS_ACTOR`.
- The requester's actor is set explicitly, with `unknown` for anything it
  did not state.
- Only an allow-list of operational variables survives, such as `PATH`,
  `HOME` and the locale. Tokens and provider credentials never reach the
  command, its telemetry, or its diagnostics.

A local execution has no `executor` block and no `assurance` field. Its
identity is self-reported by the local process, exactly as before.

## Continuing another actor's work

A work transition links only executions that the acting process may
continue (`ActorResolution.mayContinue`). That means the same actor, and
nothing known about the run (session, conversation, or CI run) differing.
Two cases follow:

- When a different agent resumes a work item, it gets a new execution whose
  `identity.parentExecutionId` names the predecessor.
- When the same agent resumes in a new session, the same happens.

A predecessor's still-active execution is never linked to the successor's
events or recovered for it. The one exception is completing the work item,
which still finalizes every active execution for that item.

The item's timeline events (`work.blocked`, `work.resumed`) are still
recorded on every active execution as clock facts about the item. Such an
event records when the item changed state. It does not attribute the
successor's work to the predecessor.

Remote requests follow the same rule. A request whose `execution.id` names
an execution the requester may not continue is refused as `domain-rejected`.

## Artifact provenance

### Canonical serialization

```yaml
provenance:
  contributions:
    EXE-20260925T194000000Z-ab12cd34:        # the execution that contributed
      operations: [created, modified]
      at: 2026-09-25T19:40:00.000Z           # first operation by this execution
      last: 2026-09-25T20:05:00.000Z         # optional: its latest recorded operation
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Initial capture"
      evidence: [EV-ROS-2026-A049]
    CTB-20260926T090000000Z-5f2e19aa:        # a human, outside any execution
      operations: [approved]
      at: 2026-09-26T09:00:00.000Z
      actor:
        kind: human
        id: kevin
derived_from: [EV-ROS-2026-A049]             # lineage (existing reference field)
```

**Why a mapping and not a list.** Contributions are a mapping keyed by
execution (`EXE-…`) or contribution (`CTB-…`) ID. The repository's front-matter
reader supports nested mappings but not lists of mappings. The keyed form also
makes "one entry per execution" a structural property.

**Operations.** `created`, `modified`, `reviewed`, `approved`, `superseded`,
and `migrated`, plus `x-...` extensions. At most one contribution may claim
`created`, and nothing may precede it.

**Timestamps.** `at` is an ISO-8601 UTC timestamp.

**Registries.** Generated registries (`registries/*.json`) project the whole
block, so provenance is queryable without parsing Markdown.

### How contributors accumulate

- **Additive only.** `provenance record` appends a new entry, or merges a new
  operation into the same execution's own entry. It never replaces or rewrites
  another contributor's entry.
- **Idempotent.** Re-recording an identical call changes nothing and writes no
  event. This also covers a human's `CTB` contribution. A later recording by the same execution updates that entry's `last`
  timestamp, so every recorded modification stays visible even when the
  execution had already recorded the same operation. This matters when an
  agent commits several times within one execution.
- **Refused edits.**
  - It refuses to re-attribute an execution to a different actor.
  - It refuses a second `created`, and a `created` recorded after existing
    history.
  - It refuses to touch provenance that is already malformed.
  - It refuses to rewrite a `derived_from` value it could not write back
    unchanged.
- **Verified before writing.** Before writing, it re-reads the result and proves
  four things: the provenance reads back as intended, `derived_from` reads
  back as exactly the existing list plus the new references, no other
  front-matter field changed, and the document body is byte-for-byte
  unchanged, including line endings.
- **Reasons.** A reason is a single line; line breaks become spaces.
- **Evented.** Every recorded change also appends an `artifact.contributed`
  event carrying:
  - the actor and the execution;
  - the operation, reason, and evidence;
  - `derivedFrom`;
  - the artifact's id, path, and `version`;
  - `previous.sha256` and `current.sha256`, which record the state the
    contribution operated on and the state it produced.

### Human and agent involvement

`./praxis provenance show ID` derives involvement from the recorded contributions,
never from "last modifier":

| Label | Meaning |
|---|---|
| `agent-created` | an agent created it; nothing since |
| `human-created, agent-modified` | a human created it; an agent changed it |
| `agent-created, human-approved` | an agent created it; a human approved it |
| `agent-created, agent-modified` | another agent revised it (`agentToAgentRevision: true`) |
| `agent-created, human-modified` | human correction of agent work (`humanCorrectionOfAgentWork: true`) |
| `origin-unknown, …` | contributions exist but none records the creation (legacy artifact later modified) |
| `unattributed` | no provenance recorded (legacy); nothing is inferred |

### Derived artifacts: authorship versus lineage

When agent B writes an artifact based on agent A's artifact, the two facts are
recorded separately:

- **Authorship.** B's execution is the `created` contribution of the new
  artifact.
- **Lineage.** The new artifact's existing `derived_from` reference field names
  A's artifact. Use `--derived-from` on `provenance record`.

`provenance show` resolves each lineage source to its own originator, and lists
the reverse direction as `derivatives`. It never merges the source's authors
into the derivative's. Lineage may name a foreign, namespaced reference from
another system (for example `ordo:resolution/r-17`), so lineage crosses
integration boundaries.

## Validation

`./praxis validate` includes provenance.

- **Errors** fail validation.
- **Warnings** are printed (and emitted with `"severity":"warning"` in
  `--json`) but do not fail validation.
- **Informational findings** appear only in `./praxis provenance audit`.

| Situation | Severity |
|---|---|
| Malformed provenance block (unknown kind or operation, bad timestamp, agent without an execution key, an agent actor missing provider/model/runtime, duplicate or late `created`) | error |
| A contribution's actor contradicts its execution record (impersonation or copy-paste) | error |
| Contribution evidence names a non-existent artifact ID | error |
| Enforced policy: artifact created on or after `requiredFrom` with no provenance | error |
| Enforced policy: requirement (`RQ`, configurable) created on or after `requiredFrom` without a `created` contribution | error |
| Enforced policy: new artifact whose `updated` date is more than one day after its latest contribution (unattributed modification; the one-day tolerance absorbs local-date versus UTC skew) | error |
| Enforced policy: new artifact changed since the base revision while its contributions did not change (unattributed modification, detected from Git independently of dates; the base is the working tree against `HEAD`, or everything since `PRAXIS_BASE_REF` in CI) | error |
| Invalid `ros.json` `provenance` block (enforced without a date) | error |
| Malformed `actor` on an event | error |
| Contribution names an execution with no local record (imported, pruned, or mistyped) | warning |
| Enforced policy: new non-requirement artifact without a `created` contribution | warning |
| Legacy artifact updated on or after `requiredFrom` without a recorded contribution | warning |
| Legacy artifact changed since the base revision without a recorded contribution | warning |
| Legacy artifact (created before `requiredFrom`) with no provenance, including any self-declared `author_agent`/`created_by_agent`/`owner_agent`/`source_author` | info |
| Legacy event that records executions but no actor | info |

### Policy and legacy repositories

Provenance is enforced per repository in `ros.json`:

```json
"provenance": {"version": "1.0.0", "enforce": true, "requiredFrom": "2026-09-25", "requireOriginator": ["RQ"]}
```

- **No `provenance` block.** Nothing is required, so an upgraded repository
  keeps validating exactly as before. Any provenance that is present is still
  checked structurally.
- **Enforced.**
  - Every canonical artifact whose `created` date is on or after
    `requiredFrom` must be attributed.
  - Artifacts created earlier are **legacy**. They stay valid and readable, and
    are never required to acquire invented history.
  - Legacy authorship fields such as `author_agent` are free-text,
    self-declared, and unverified. `audit` and `show` report them under that
    label; Praxis never converts them into structured provenance.

**Migration** is deliberately a no-op. Praxis does not rewrite history and does
not infer authorship from style, timestamps, filenames, or Git metadata. A
legacy artifact gains provenance only when a real contributor records a real
contribution: its first modification after the policy date adds an
`origin-unknown` history whose later entries are known.

**Legacy records elsewhere.**

- Execution records written before `identity.actorKind` existed are projected
  from the discovery mechanism the record itself preserved
  (`provenance.sources`), which is recorded evidence, or else as `unknown`.
- Events written before `actor` existed remain valid.
- Backlog items without `createdByActor` remain valid.

## Integrations and export

Provenance travels with data rather than being stripped at a boundary.

- **Work adapter publication.** `./praxis adapter publish` copies events verbatim,
  so the external work system receives `actor` on work events and the full
  `artifact.contributed` records.
- **Registries.** Generated registries include each artifact's `provenance`
  block and `derived_from`.
- **Portable files.** The artifact file itself carries its contributors, so
  copying it to another repository or system keeps its origin. A receiving
  repository without the originating execution records reports those
  contributions as a *warning*, not an error, and keeps them intact.
- **Ordo.** Ordo resolution observations already carry `provider`
  `{id, model, …}`, and assessments carry a provider-neutral `assessor`. Praxis
  preserves raw Ordo records verbatim. `./praxis ordo handoff` adds
  `producedBy: {actor, execution}`: the packaging actor in canonical form and
  its execution when exactly one active execution is evidently the same run
  (otherwise `null`, never a guess), so the receiving agent or system keeps
  the handoff's origin.
- **Installation.** The `ROS-INSTALL-*` bookkeeping event that both installers
  write is attributed to the installer itself,
  `{"kind":"automation","id":"ros-bootstrap","runtime":"ros-bootstrap"}`, so
  no event in a fresh repository lacks an actor.
- **Other Echelon systems.** Vigila, Aegis, Dokimos, Percepta, and EDF
  experiments can adopt the same actor object
  (`schemas/provenance-actor.schema.json`) and contribution shape
  (`schemas/artifact-provenance.schema.json`). There is no dependency on any of
  them: Praxis remains independently usable, and those systems need only the
  JSON shapes.
- **Git host neutrality.** Nothing here assumes GitHub. `github-actions` is one
  whitelisted automation runtime among others, and a future attestation from a
  GitHub App or OIDC token would be one attestation source among others.

## Metrics

This work adds no analytics subsystem. It records the facts that later
analysis needs.

`./praxis provenance audit --json` emits:

- one row per (artifact, contribution): artifact id, kind, path, contribution
  key, execution, operations, time, actor, and whether it is the origin;
- per-actor summaries: artifacts, created, modified, reviewed or approved, and
  executions;
- coverage counts for artifacts, events, executions, and backlog items;
- `collaboration`: the artifacts where one agent revised another agent's work
  (`agentToAgentRevisions`), where a human corrected agent work
  (`humanCorrectionsOfAgentWork`), where a human approved agent work
  (`humanApprovedAgentWork`), and `hotspots` (artifacts with more than one
  contribution, most first). Each is derived from the artifact's recorded
  involvement, never from the last modifier.

Joined with telemetry execution records (cost, duration, and tokens by
execution) and with defect or validation data by path or ID, these rows
support the following analyses by agent, model, or provider:

- requirements created or modified;
- findings;
- rework and modification hotspots;
- human corrections of agent work and agent-to-agent revisions;
- evidence generated;
- cost, duration, and outcomes.

## Examples

**Two agents and a human on one requirement.**

```bash
# Claude Code session (identity discovered from CLAUDE_CODE_SESSION_ID)
./praxis work begin --id FEAT-9 --occurred-at "$(date -u +%Y-%m-%dT%H:%M:%S.000Z)"
./praxis provenance record --id RQ-APP-2026-A007 --operation created --reason "From customer interview" --derived-from EV-APP-2026-A002
./praxis work complete --id FEAT-9 --occurred-at … --evidence implementation=… --evidence tests=…

# Codex session, later (identity discovered from CODEX_SESSION_ID)
./praxis work begin --id FEAT-12 --occurred-at …
./praxis provenance record --id RQ-APP-2026-A007 --operation modified --reason "Tighten acceptance criteria"

# A human reviewer, outside any execution
PRAXIS_ACTOR_KIND=human PRAXIS_ACTOR=kevin ./praxis provenance record --id RQ-APP-2026-A007 --operation approved

./praxis provenance show RQ-APP-2026-A007
# involvement: agent-created, agent-modified, human-approved
```

**A local or future agent with no whitelisted environment** declares itself
once:

```bash
export PRAXIS_ACTOR_KIND=agent PRAXIS_ACTOR=acme-planner PRAXIS_TELEMETRY_PROVIDER=acme PRAXIS_TELEMETRY_RUNTIME=acme-cli
./praxis work begin --id FEAT-3 --occurred-at …    # model left unset: recorded as "unknown", never invented
```

**CI automation** needs no declaration. Under GitHub Actions the actor resolves
as `{"kind":"automation","id":"github/github-actions",…}`, and a CI job that
records provenance is attributed to automation, not to an agent.

## Continuation and handoff between executors

Continuity never depends on a provider's chat history (`DF-ROS-2026-A042`).
When an executor disappears, the successor inherits nothing but the
repository and Praxis state:

- **Recovery.** The successor reads the latest durable checkpoint: the exact
  commit, branch, completed work, and next action. Praxis re-verifies it
  against the remote.
- **Its own execution.** The successor runs `work continue` under its own
  identity. The new execution records `parentExecutionId` as the
  predecessor's execution: lineage, not authorship.
- **The predecessor stays as it was.** The predecessor's execution record is
  never edited. A `work.continued` event, attributed to the successor,
  records the predecessor's disposition as `interrupted`, observed by the
  successor. The predecessor is never marked successful, and the successor
  never claims its work.
- **Checkpoints are attributed.** Each checkpoint carries the actor and the
  execution that recorded it. In a handoff from A to B, A's checkpoints stay
  A's and B's stay B's.

Any pair of executors works the same way, because each keeps its own
identity:

- agent to agent, across providers (for example Claude to Codex, or Codex to
  Gemini);
- agent to human, and human to agent;
- automation to either.

A process with no declared identity cannot continue work. A successor can
never continue as another executor.
