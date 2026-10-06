# Adaptive development telemetry

Praxis telemetry connects intent, execution, repository change, evidence, and result without making any agent vendor canonical. `./praxis work begin` starts an execution record automatically and `./praxis work complete` finalizes every active execution for that work item. One work item may therefore contain sequential, parallel, resumed, handed-off, human-assisted, or provider-mixed executions.

The canonical local record for each run is `.ros/telemetry/executions/<execution-id>.json`. Records are deliberately segmented rather than appended to one unbounded document. They are suitable for later publication, but this version does not implement a central telemetry service.

## Execution lifecycle

The standard lifecycle is:

```text
work begin
  -> execution start and runtime/capability discovery
  -> work classification and Git baseline
  -> optional runtime snapshots, metrics, events, scope, and quality signals
  -> work complete
  -> final Git snapshot and deterministic metrics
  -> execution finalization
  -> telemetry validation with normal Praxis validation
```

`work block` and `work resume` add interruption events and allow Praxis to derive blocked duration. A resumed item whose prior execution was already finalized receives a new child execution. `work continue` (a successor taking over active work whose executor disappeared) also creates a new execution whose `identity.parentExecutionId` names the predecessor, and records the predecessor as interrupted in a `work.continued` event, never in the predecessor's own record. See "Durable checkpoints and continuity" in `work-protocol.md`. An agent handoff or a parallel/subagent run can be represented explicitly with `telemetry start --parent-execution`, `--agent`, and `--subagent`.

Historical work records created before telemetry existed remain valid. The compatibility boundary is explicit: once a work item has `telemetryExecutionIds`, completed work requires those records to be finalized. Praxis does not invent telemetry for older history.

## Execution steps

A step is a meaningful unit of the agent's actual execution plan inside one execution—for example, inspect requirements, implement a change, run tests, or review the result. It is execution evidence, not a requirement or separate work item. Do not create a step for every command or keystroke, and do not use a step to hide an independently governable obligation.

Steps are created as the agent works; no central plan is required. Praxis preserves the supplied name and description as historical evidence. The lifecycle is state-directed:

```text
planned -> active -> completed
   |          |  \-> blocked -> active
   |          |       \-> abandoned
   \----------+-----------> abandoned
```

Illegal and repeated transitions are rejected. Without a parallel-step protocol, active steps form one ancestor path: an active parent may have one active nested child, but unrelated active siblings cannot coexist. Nested parents are grouping steps; normalized usage and cumulative checkpoints belong to leaves, so parent and child intervals cannot both enter execution totals. A measured step cannot later acquire children. A parent cannot finish while a descendant is unresolved, and an execution cannot finalize while any step is planned, active, or blocked. After a crash, the unresolved step remains visible and must be resumed, completed, blocked, or abandoned explicitly.

```bash
./praxis step begin --name "Inspect implementation" \
  --classification research --occurred-at 2026-09-27T12:00:00Z
./praxis step begin --name "Inspect parser" --parent STEP-... \
  --classification implementation --occurred-at 2026-09-27T12:02:00Z
./praxis step complete --occurred-at 2026-09-27T12:05:00Z
./praxis step complete --occurred-at 2026-09-27T12:06:00Z
./praxis step list --work-item WI-0065 --json
./praxis step show STEP-...
```

Classifications are open strings; `planning`, `research`, `requirements`, `design`, `implementation`, `testing`, `debugging`, `review`, `documentation`, `governance`, `integration`, `deployment`, and `other` are useful conventions, not a closed enum. Use a precise custom value rather than misclassifying work. `parentStepId` is optional, so ordinary flat execution remains simple.

Each step snapshots the execution actor and available provider/model/runtime identity. Mutations require positive evidence that the current process owns the execution and session, even when an execution ID is supplied explicitly. The execution record stores each metric once; step records refer to canonical `measurementId` and sanitized `snapshotId` values. This prevents execution totals and nested parent/child views from double-counting the same observation.

Use `step record` only for a value the named source actually exposes. Use `step availability` for `supported-unavailable`, `unsupported`, or `unknown`; never encode those states as zero. A late observation may be attached to a terminal step only with an explicit step ID. Praxis never assigns telemetry to a step merely because timestamps overlap.

```bash
./praxis step record --metric tokens.input --value 0 --quality observed \
  --source-type runtime-api --source-name provider-usage --source-mechanism reported \
  --collected-at 2026-09-27T12:05:00Z
./praxis step availability --metric tokens.reasoning --status supported-unavailable \
  --reason "provider response omitted this category" --occurred-at 2026-09-27T12:05:01Z
```

For a reliable cumulative provider counter, explicitly checkpoint existing canonical observations. An end checkpoint derives a delta only when there is exactly one compatible begin observation with the same cumulative metric, unit, currency, and source, and the counter did not decrease. The derived metric names both source measurements and the arithmetic method. Missing, ambiguous, incompatible, or decreasing observations produce an explicit `insufficient` result, not a guessed delta.

Cost uses the same quality vocabulary. Provider-reported amounts are `observed`; arithmetic is `derived`; uncertain calculations are `estimated` with confidence. Calculated costs require pricing source and version (plus effective date, model, method, and token-measurement references when applicable). A delta of a provider-reported cumulative cost names `provider-cumulative` as its derivation source; it does not pretend Praxis applied a price table.

`step link` records sourced references to files observed or changed, commits, commands, tests, validations, evidence, decisions, requirements, and outcomes. The default source is `agent-report`. A relationship does not transfer authorship and does not claim another contributor's pre-existing file change.

## Capability states and zero

Each known normalized metric has a capability state for the execution:

| State | Meaning |
|---|---|
| `supported-observed` | the runtime supports the metric and supplied a value |
| `supported-unavailable` | the runtime family can expose it, but this run did not |
| `unsupported` | the runtime says it cannot provide the metric |
| `unknown` | Praxis or the runtime has not mapped the capability |
| `derived` | Praxis can calculate it from a named deterministic source |
| `estimated` | a value exists but is an estimate, with confidence |

An observed value of zero is a real metric. Unavailability is represented only by capability state and reason, never by a synthetic zero. Validation rejects nonnumeric values, negative counts, missing sources, incompatible units, estimates without confidence, and several impossible scope/aggregation combinations.

The capability object is a current-state projection with bounded transition history. `discoveredAt` and `lastAssessedAt` retain the source assessment time; `recordedAt` records when Praxis processed it, so delayed or batch telemetry does not have to pretend it arrived chronologically. When a capability changes from observed to unavailable (or the reverse), the prior state, reason, source, and timestamps move into `history`. The configured history cap keeps the record bounded, retaining the first and most recent transitions plus `historyOmitted` when intermediate transitions are collapsed.

## Normalized and raw layers

The normalized vocabulary is data-driven by [`telemetry/metrics.json`](../telemetry/metrics.json). Every measurement records value, unit, quality (`observed`, `derived`, or `estimated`), aggregation rule, scope, source name/type/mechanism, collection time, schema version, optional dimensions, and currency/confidence where applicable.

Praxis also normalizes baseline/ending dirty-file counts, renamed and binary-file changes, time to first token, model-request failures, approval requests/denials, and peak runtime memory/CPU when available. These go beyond simple token and file totals because they help explain attribution reliability, latency, retry/permission friction, and resource cost. They are intentionally metadata-only; Praxis does not collect prompt text, command output, or file contents to improve those measures.

Raw snapshots retain legitimate provider data that an adapter does not yet understand. Praxis recursively redacts credentials, prompts, messages, content, command arguments/output, transcript paths, absolute working paths, and email fields and truncates large strings. Retention is bounded by per-input, per-snapshot, per-execution snapshot-count, and per-execution byte limits. If a sanitized payload exceeds a retention budget, Praxis omits the value but still stores normalized measurements, unmapped field paths, redaction counts, a content-free idempotency event, and the explicit omission reason. The sanitized snapshot lists unmapped leaf fields and redactions. Unknown fields do not fail ingestion.

For example, if a provider adds `usage.quantum_cache_tokens` tomorrow, the existing adapter stores the sanitized field under `rawTelemetry`, surfaces its path as an `unknown` capability, and increments `telemetry.unknown_fields`. Nothing is silently converted or discarded. A later compatible registry/adapter release can promote the field while old raw records remain readable.

If repository policy disables raw-payload retention, Praxis still records a content-free ingestion event, the discovered field path, its `unknown` capability, the unknown-field/redaction counts, and `telemetry.raw_snapshots_omitted`. The value is deliberately not retained under that stricter policy, but retry idempotency and capability discovery remain intact. The same behavior applies when an execution reaches `maxRawSnapshotsPerExecution` or `maxRawBytesPerExecution`; each event identifies `repository-policy-disabled`, `snapshot-byte-limit`, `execution-snapshot-limit`, or `execution-byte-limit` as the cause.

## Commands

The automatic lifecycle is enough for deterministic baseline/final metrics. Runtime integrations can add higher-fidelity data:

```bash
# Start another execution for the same active work item.
./praxis telemetry start FEAT-142 \
  --provider anthropic --runtime claude-code --session session-123 \
  --classification development --parent-execution EXE-...

# Ingest provider output, hook JSON, a status-line snapshot, or a generic envelope.
./praxis telemetry ingest FEAT-142 --adapter openai-codex --input codex-events.jsonl
./praxis telemetry ingest FEAT-142 --adapter anthropic-claude-statusline --input status.json
./praxis telemetry ingest FEAT-142 --adapter anthropic-claude-session --input ~/.claude/projects/PROJECT/SESSION.jsonl
./praxis telemetry ingest FEAT-142 --adapter google-gemini-otel --input otel.jsonl
./praxis telemetry ingest FEAT-142 --adapter github-copilot-hook --input - --quiet

# Record an explicitly sourced metric. Estimates require --confidence.
./praxis telemetry record FEAT-142 --metric tests.passed --value 84 \
  --source-type external-tool --source-name node-test --mechanism tap-summary

# Record the cost the execution platform reports for the session.
./praxis telemetry record FEAT-142 --metric cost.execution_total --value 9.48 \
  --currency USD --quality observed \
  --source-type platform --source-name claude-code-remote --mechanism session-record

# Apply a multi-valued classification and factual R&D context.
./praxis telemetry classify FEAT-142 \
  --classification research-development --classification experiment \
  --rd-context rd-context.json --evidence-link EX-ROS-2026-A001

./praxis telemetry show FEAT-142
./praxis telemetry summary FEAT-142
./praxis telemetry adapters
```

`--input -` reads JSON from standard input and `--quiet` keeps hook stdout clean. Input may be one JSON value or JSON Lines. Confidence may be a defensible numeric value from 0 through 1 or the categorical label `low`, `medium`, or `high`; prefer a category when the runtime does not publish calibrated confidence. A calculated cost also requires `--pricing-source` and `--pricing-version` so changing price tables remain auditable. The `generic` adapter accepts this provider-neutral envelope:

```json
{
  "schemaVersion": "1.0.0",
  "snapshotId": "provider-run-42-final",
  "collectedAt": "2026-09-05T01:02:03Z",
  "identity": {"provider": "future-ai", "runtime": "future-cli", "sessionId": "s-42"},
  "capabilities": [
    {
      "metricId": "tokens.input",
      "status": "supported-observed",
      "source": {"type": "runtime-api", "name": "future-cli", "mechanism": "usage-response"}
    }
  ],
  "metrics": [
    {
      "id": "tokens.input",
      "value": 0,
      "unit": "tokens",
      "quality": "observed",
      "scope": "turn",
      "source": {"type": "runtime-api", "name": "future-cli", "mechanism": "usage-response"}
    }
  ],
  "raw": {"usage": {"quantum_cache_tokens": 17}}
}
```

Generic input can also update `classification`, `scope`, `links`, and `qualitySignals`. Stable IDs should be used for requirements, acceptance criteria, evidence, experiments, decisions, defects, dependencies, commits, and pull requests.

## Current provider integration surfaces

Provider capabilities evolve; treat these as adapter guidance, not permanent assumptions.

- **OpenAI Codex:** `codex exec --json` emits JSON Lines with per-turn usage that the `openai-codex` adapter maps to input, output, cached-input, cache-write, reasoning, and total token metrics when present. Interactive Codex environments may expose session and thread IDs to the repository process without exposing token or cost totals; in that case Praxis records identity and `supported-unavailable`, never zero. [Codex JSON event source](https://github.com/openai/codex/blob/main/codex-rs/exec/src/exec_events.rs)
- **Anthropic Claude Code session transcript:** see "Session transcript metrics" below (`anthropic-claude-session`).
- **Anthropic Claude Code:** the status-line JSON supplies session/runtime/model identity, an estimated cumulative session cost, and current-context/cache gauges. Hooks supply lifecycle, tool, compaction, permission, and subagent events. Claude Code OpenTelemetry adds request tokens, costs, retries, active/API time, tool outcomes, and request IDs; ingest transformed JSON with `anthropic-claude-otel`. [Status-line fields](https://code.claude.com/docs/en/statusline), [hooks](https://code.claude.com/docs/en/hooks), [monitoring](https://code.claude.com/docs/en/monitoring-usage)
- **Google Gemini CLI:** hooks expose session and tool lifecycle JSON. Its OpenTelemetry stream exposes input/output/thought/cache/tool tokens, file and line operations, tool latency/outcome, chat compression, model routing, agent turns/duration, memory, and CPU. Keep prompt logging disabled and transform the exporter JSON to the documented attributes before ingestion. [Gemini hooks](https://github.com/google-gemini/gemini-cli/blob/main/docs/hooks/reference.md), [Gemini telemetry](https://github.com/google-gemini/gemini-cli/blob/main/docs/cli/telemetry.md)
- **GitHub Copilot CLI/cloud agent:** repository hooks expose session, tool, error, main-agent, and subagent lifecycle events. Copilot OpenTelemetry follows GenAI semantic conventions and can emit model/tool traces and token metrics. Content capture is not required by Praxis and should remain disabled. [Copilot hooks](https://docs.github.com/en/copilot/reference/hooks-reference), [Copilot OpenTelemetry](https://docs.github.com/en/copilot/concepts/agents/opentelemetry)
- **Local/self-hosted and future runtimes:** set the `PRAXIS_TELEMETRY_*` identity environment variables (the legacy `ROS_TELEMETRY_*` names still work) and ingest the generic envelope. Provider-specific extensions belong at this edge. New unmapped fields are retained without changing the core execution model.

Praxis does not install vendor hooks automatically: repository-level hook files can execute with developer privileges and may collide with existing project policy. The provider router files (`CLAUDE.md`, `GEMINI.md`, and `.github/copilot-instructions.md`) only point to the canonical `AGENTS.md`; hook enablement remains an explicit, reviewable repository decision.

## Session transcript metrics (PRAXIS-PLAN-05)

`--adapter anthropic-claude-session` promotes the `EX-ROS-2026-A021` harness
script (`research/experiments/EX-ROS-2026-A021-harness/session_metrics.py`)
into Praxis, so context overhead and cold-start cost become measurable
(`EV-ROS-2026-A064`). The input is a Claude Code session transcript (JSON
Lines, `~/.claude/projects/*/SESSION.jsonl`). Derivation is deterministic
(`Praxis.Domain.Telemetry.SessionTranscript`); nothing is inferred by a model.

| Metric | Quality | How it is derived |
| --- | --- | --- |
| `model.requests` | observed | distinct requests (message ID) carrying usage; usage repeated per content block counts once |
| `tokens.input`, `tokens.output`, `tokens.cache_read`, `tokens.cache_write` | observed | summed over those requests |
| `tool.calls`, `tool.failures`, `tool.shell_commands` | observed | tool-use blocks, error tool results, `Bash` calls |
| `tool.file_reads`, `tool.searches`, `tool.file_writes`, `tool.build_executions`, `tool.test_executions` | derived | `Read`/`Grep`/`Glob`/`Edit`/`Write` calls plus `cat`/`head`/`tail`/`sed -n`, `grep`/`rg`/`find`, redirections and build/test commands pattern-matched at the start of shell commands |
| `context.compactions` | observed | compaction system entries and compact summaries |
| `context.repeated_file_reads` (new) | derived | reads beyond the first of each file: context re-acquired in the session |
| `context.governance_reads` (new) | derived | reads of `AGENTS.md`, `CLAUDE.md`, `docs/00-governance/`, the work-protocol, planning, CLI, telemetry and provenance guides and `requirements/PLANNING-WORK-GROUPS.md` |
| `time.first_code_change_ms` (new) | derived | first transcript entry to the first change under `src/` or `tests/`: the session's cold start |
| `time.active_ms` | derived | sum of gaps between consecutive entries, leaving out gaps over 15 minutes (idle: waiting on a person, a permission prompt or an orchestrator) |
| `cost.session_cumulative` | estimated | the runtime's own `cost-state` estimate, USD, confidence `medium` |

A metric the transcript does not carry is `supported-unavailable`, never
zero. The raw snapshot is a content-free summary (session ID, models, span,
per-file repeated and governance read counts, per-tool call counts), never
the transcript: prompts, messages, tool input and output, commands and the
working directory are not stored. Because a transcript is far larger than a
status-line snapshot, this adapter's input may be up to 64 MiB; every other
adapter keeps the configured raw-payload budget.

The snapshot ID is `claude-session-SESSION_ID`, so a session is ingested into
an execution once and its sums are never counted twice: ingest it when the
session's work on the item is done, before `work complete`. A session that
worked on several items should be ingested into one of their executions, not
all of them. Platform-reported cost is not in the transcript: record it with
`telemetry record --metric cost.execution_total --quality observed` (above).
`./praxis plan` reads these metrics: `time.active_ms` corrects productive time,
`time.first_code_change_ms` and the read counts price the cold starts
grouping avoids, and `cost.execution_total` is observed monetary evidence
(see `docs/planning.md`).

## Work and R&D classification

Classification is multi-valued. The core vocabulary is: Research, Development, Research & Development, Maintenance, Defect/Bug Fix, Investigation/Diagnostic, Architecture/Design, Documentation, Testing/Verification, Infrastructure/DevOps, Security, Operational/Support, Refactoring, Experiment, Prototype/Proof of Concept, and Administrative/Process. Records store their lowercase stable identifiers. Namespaced `x-...` extensions allow a domain to add a future category without changing the core vocabulary; other unknown values fail validation.

`research-development` requires factual context such as a research question, hypothesis, technical uncertainty, experimental objective, or knowledge gap. The `rd` object can additionally preserve alternatives, controls/comparisons, evidence, results, limitations, failed approaches, resulting knowledge/capability, resolution state, and follow-up experiments. This records facts for later analysis; it is not a tax or legal eligibility determination.

`scope.initial` and `scope.actual` can preserve expected versus discovered root cause, files/components, blockers, complexity, time/cost, dependencies, discoveries, new work, variance, and variance reason. Trivial work can leave these objects empty. `qualitySignals` records how a correction was detected—compiler, type system, test, static analysis, architecture check, runtime, agent self-review, human review, escaped defect, mutation test, or Praxis state guard—only when the source is known.

## Mechanical and cooperative guarantees

| Capture | Guarantee |
|---|---|
| execution ID, work-item link, start/final times, wall/blocked duration | mechanical when work uses the Praxis CLI |
| repository ID, branch, starting/ending SHA, dirty-state counts | mechanical when Git is available |
| commits, files, lines, extensions, test-file changes, documentation changes | mechanical only from a clean execution baseline; otherwise explicitly unavailable because pre-existing edits prevent trustworthy attribution |
| completion finalization and structural validation | mechanical for work items that have entered the telemetry contract |
| provider/model/runtime/session identity | discovered from a small whitelist of non-secret environment fields, adapter input, or explicit flags; unknown values remain explicit |
| actor kind (`identity.actorKind`: agent, human, automation, unknown) | explicit `--actor-kind`/`PRAXIS_ACTOR_KIND`, else implied only by a whitelisted agent runtime (agent) or CI (automation); otherwise `unknown`. Records predating the field are projected from `provenance.sources`; see `agent-provenance.md` |
| tokens, cost, model/tool time, turns, retries, subagents, and detailed tool events | mechanical only when a provider runtime stream, hook, API, or OpenTelemetry exporter is connected |
| research context, failed approaches, scope variance, requirements, decisions, and evidence links | dependent on agent/human/work-system reporting unless a domain tool exposes them |

Git metrics intentionally fail open to `supported-unavailable` when the execution began dirty. Praxis will not attribute another contributor's pre-existing changes to the current run. The record still retains start/end repository state and the reason the execution delta is unavailable.

## Persistence and synchronization

Praxis writes JSON and derived queue Markdown through same-directory temporary files followed by atomic replacement, so an interrupted writer leaves either the prior complete file or the next complete file rather than partially parsed JSON. Work-context transitions share one local lock, execution creation has an identity-index lock, and every mutation of an existing execution is serialized by execution ID. Locks carry a process identity and random ownership token; an abandoned owner can be reclaimed without allowing an older writer to delete a replacement lock. `.ros/locks/` is transient, Git-ignored, and excluded from development metrics.

These guarantees are local-filesystem guarantees, not a distributed transaction protocol. Creation of an execution file and its backlink in work context are two atomic replacements; validation checks both directions so a process failure between them becomes an explicit repairable finding. Contended operations time out rather than stealing a lock from a live process. Parallel provider callbacks to one execution and parallel execution starts against one work item have regression coverage. Independent machines must use separate worktrees or a future central coordinator; network-filesystem lock semantics are not claimed.

## Aggregation and comparison

The metric registry declares `sum`, `maximum`, `latest`, `latest-per-session`, or `none` for every normalized metric. Cumulative session totals use `latest-per-session`: Praxis keeps the latest snapshot for each provider/runtime/session tuple and then combines distinct tuples. It never sums repeated cumulative snapshots, including when two executions on one work item share a session, and it does not collapse coincident session-ID strings from different providers. Per-turn and per-operation deltas use `sum`; repository dirty-state gauges use `maximum`; context gauges use `none` because a scalar cross-session total would have no valid meaning. Per-execution measurements remain available in every case.

`time.wall_ms` is execution effort, so its work-item sum can exceed elapsed calendar time when agents overlap. Work-item summaries therefore include a separate `timing` object with earliest start, latest finalization, calendar span, total execution wall time, and overlapping execution milliseconds. Incomplete work returns null aggregate durations rather than a misleading partial total. Likewise, `tests.passed` counts reported passing results across executions; rerunning the same tests increments it and does not imply unique test-case count.

Cross-provider comparisons must account for semantic differences. Providers may include reasoning in output tokens, define cache categories differently, estimate costs with a changing price table, route a configured model to a different served model, or expose current context rather than cumulative usage. Preserve the raw snapshot and source; do not coerce metrics merely to fill a comparison table.

Step-attributed observations participate in execution and work-item aggregation because they are canonical execution metrics carrying `stepId`, not copied rollups. Parent grouping steps never carry or implicitly include child measurements. `telemetry summary` adds an exact `stepCount` when matching executions contain steps; token and cost totals remain grouped by metric/unit/currency/quality semantics rather than collapsed into a misleading scalar. Step views retain per-measurement quality and availability, so future analysis can derive tokens or cost per completed step, accepted requirement, defect, successful test, or transition, and can compare rework/implementation/testing shares without storing those ratios prematurely.

## Fallback execution

The approved `protocol/praxis-envelope-v1.schema.json` fallback remains a second entry path into the same model, not a second authority. Its optional `execution.steps` records preserve identity, order, nesting, transitions, measurement availability, raw provider objects, and evidence. Old envelopes without `execution` remain valid. Reconciliation validates boundaries, sequence, timestamps, availability/value consistency, duplicate measurement identity, work-item/execution identity, and optional `praxisInstanceId` before dispatch. If a locally authoritative instance identity exists, a conflicting claim is rejected.

`./praxis reconcile --envelope FILE` maps `work.start|begin`, `work.block`, `work.resume`, and `work.complete` requests through the native work planner and completion-evidence checks. It first renders the complete result in isolation. A replayable journal then commits context, events, completion queue projection when applicable, the optional canonical execution/steps, and the applied receipt as one exact Git path set; the deterministic `praxis-reconcile/<transaction-id>` tag is the completion checkpoint. A killed process resumes that journal before reading another envelope, so it never dispatches a transition twice. Accepted input is removed only after the checkpoint exists; rejected input is quarantined with diagnostics and cannot partially mutate canonical state. Raw fallback payloads pass through the same sensitive-field redaction and size validation as native ingestion.

One envelope owns one work item and branch. Its base commit must exist and be an ancestor of the observed HEAD, its timeline and requests must align in order and time, and its claimed instance identity must agree with the local instance when both exist. Provider/model/runtime values and measurements are preserved only as supplied. Zero remains an observed value; unavailable/unsupported/unknown measurements create capability evidence without a metric value. See [`fallback-reconciliation.md`](fallback-reconciliation.md) for the runtime-free procedure and retry semantics.

## Privacy, storage, and schema evolution

Capture metadata, not content. Never enable prompt/response/tool-argument capture solely for Praxis. Do not place credentials or authentication headers in adapter files. Default raw retention is at most 256 snapshots and 8 MiB of sanitized payload per execution, with 256 KiB per input/snapshot and 64 capability-history entries; repository configuration may lower those budgets. Long-term rotation or external object storage is deferred until measured scale demonstrates a need. Teams publishing records later should define retention, access control, reconciliation, signing, and deletion policies in the central system.

Telemetry schema `1.0.0` is additive at the raw boundary. Readers reject an unknown record schema rather than guessing, while work history with no telemetry remains valid. To normalize a newly useful field:

1. confirm its provider semantics, unit, scope, and cumulative/delta behavior;
2. add a compatible metric definition to `telemetry/metrics.json`;
3. update an edge adapter mapping without deleting the raw field;
4. add observed, unavailable, zero, aggregation, and version tests;
5. document comparison limitations and pricing provenance where relevant.

Breaking identity, quality, unit, or aggregation meaning requires a new telemetry schema major version and a migration that preserves the original record.

## Current limitations

Praxis cannot obtain hidden reasoning cycles, self-corrections, precise active-versus-waiting time, human interruption time, authoritative billed cost, model rerouting identity, or detailed tool activity unless the runtime exposes them. This Codex desktop environment exposes session/thread identity to repository commands but not its in-app token/cost counters. Provider hooks may miss UI-only actions, and exporter formats may change. Git-delta attribution also assumes one execution owns its working tree; concurrent actors in the same checkout require separate worktrees or a richer attribution mechanism. Agent-reported findings remain lower-assurance than runtime or deterministic Git/test output.

Raw values can still contain a secret under a novel innocuous key, so upstream content suppression remains mandatory. Metric and event arrays are not capped because silently discarding normalized evidence would be worse without measured thresholds; very high callback volume will eventually make whole-record rewrites expensive even though raw payload and capability history are bounded. The accepted design is per-execution JSON, not an append-only event store. Reopen segmentation/compaction when real records approach retention limits, lock contention becomes routine, or profiling shows write amplification is material. Local records also remain unsigned, not centrally reconciled, and not globally deduplicated.

## Steps, evidence quality, and usage (PRAXIS-REMOTE-04)

**Steps.** A step is a unit of work inside an execution. It lets work,
evidence and usage be attributed below the execution level. Steps are
recorded as events on the execution record, so a step inherits the
execution's identity and never carries one of its own:

```
praxis telemetry step start    [TARGET] --step STEP-ID [--name TEXT]
praxis telemetry step complete [TARGET] --step STEP-ID [--reason TEXT]
praxis telemetry step fail     [TARGET] --step STEP-ID [--reason TEXT]
praxis telemetry record [TARGET] --metric ID --value V ... --step STEP-ID
```

This event-only command family is the compatibility surface used by remote
protocol 1.1. It remains queryable through `telemetry show`, `telemetry usage`,
checkpoint views, and effective-current segmentation, but it does not invent
the richer planned/nested ledger fields exposed by `praxis step`. New
agent-authored execution plans should use `praxis step`; event-only histories
remain valid and are never backfilled.

The caller chooses the step ID, so repeating a transition is an idempotent
no-op. Illegal transitions are refused:

- completing or failing a step that was never started;
- failing a step that has already completed.

A step-scoped measurement carries the `step` dimension, and it is refused
if its step was never started in that execution.

**Evidence quality.** Every measurement already records a `quality` and a
`source.type`. The vocabulary from #90 is projected from those two fields;
there is no competing field. The projection never upgrades a value.

| Projected quality | Condition |
|---|---|
| measured | Source type `ros-git`, `ros-clock` or `environment`. This is Praxis's own observation. |
| provider-reported | Source type `runtime-api`, `runtime-output`, `runtime-hook` or `external-tool`. |
| agent-reported | Source type `agent-report`. |
| human-reported | Source type `human-report`. |
| calculated | Source type `calculated`, or quality `derived`. |
| estimated | Quality `estimated`. This wins over every source type. |
| unavailable | The metric has no measurement. Its capability status records why. |

Remotely supplied telemetry keeps the source type the requester asserted,
or `agent-report` when it asserted none. A request can never claim a source
type that only Praxis can observe.

**Usage report.**

```
praxis telemetry usage [WORKITEM] --by work-item|execution|step|provider|model|day
```

The report sums the registry's additive (`sum`) metrics for each group and
says what backs each total:

- the number of measurements;
- how many have each evidence quality;
- which executions reported the metric;
- which executions in the group reported nothing (`unavailableExecutions`).

A group whose executions reported nothing has a `total` of `null`, not `0`.
A total with any unavailable executions is marked `complete: false`, which
means it is a lower bound.

The existing `telemetry summary` is unchanged.

## Effective-current step telemetry (PRAXIS-CONT-11)

**New observability is effective-current. Praxis preserves truthful
historical gaps rather than restarting work or fabricating telemetry.**
Historical absence of step data is not an invalid execution, and unknown
historical step attribution is not zero step usage.

An execution's telemetry has one of two legitimate granularities:

| Term | Meaning |
|---|---|
| execution-level telemetry | measurements with no `step` dimension; attributed to the execution as a whole |
| step-level telemetry | measurements recorded against a started step of the same execution |
| unavailable historical step attribution | usage recorded before step tracking was adopted: known per execution, unknown per step |

Step tracking can be adopted at any point of an execution by starting a
step. Nothing restarts. The boundary is derived from the execution's own
first `step.started` event, so no field is backfilled and no history is
rewritten:

- measurements recorded before adoption stay execution-scoped exactly as
  recorded;
- Praxis never creates synthetic historical steps, and never splits
  earlier usage among later steps by any proportion, duration or guess;
- an execution that never adopts steps stays valid, and so does its work
  item's completion;
- a successor (`work continue`) starts its own execution and its own steps;
  nothing is appended to its predecessor.

`work context`, `status` and `work continue` report this per execution in
the continuity block's `telemetry.executions[]`:

| Field | Meaning |
|---|---|
| `segmentation` | `execution-level` (no step ever recorded), `step-level` (steps from the execution's start) or `step-level-adopted` (steps adopted partway) |
| `stepTrackingStartedAt` | the first step's start, or `null` |
| `executionScopedBefore` | `{from, until}` of the execution-scoped period before adoption; `from` is `null` when the execution's start is unknown |
| `historicalStepAttribution` | `unavailable` when some activity has no step attribution, else `not-applicable` |
| `measurements` | counts of `executionScopedBeforeSteps`, `executionScopedOutsideSteps` and `stepScoped` measurements |

`--text` renders it as, for example:

```
TELEMETRY SEGMENTATION
  EXE-... (active): execution-level before: 2026-09-29T10:11:41.946Z .. 2026-09-29T10:11:55.000Z (step attribution unavailable); step-level from: 2026-09-29T10:11:55.000Z
```

In `telemetry usage --by step`, execution-scoped usage is the
`(outside any step)` group. Every execution with an execution-scoped period
belongs to that group, so one that reported nothing there is listed in
`unavailableExecutions` (unknown, `complete: false`), never counted as zero.

`validate` accepts measurements without a step whatever the execution's
segmentation, and reports a measurement whose `step` names a step its own
execution never started.

## Steps and durable checkpoints (PRAXIS-CONT)

A durable checkpoint (`work checkpoint`, see `work-protocol.md`) may name a
step with `--step STEP-ID`. Praxis refuses the link unless the step was
started in the checkpointing process's own execution, and that execution
belongs to the work item. `validate` re-checks the link offline.

Steps and checkpoints are not coupled otherwise:

- A material implementation step that ends after repository changes should
  normally be followed by a checkpoint.
- Research and analysis steps that change nothing need none.
- Praxis never checkpoints automatically and never requires a synthetic
  step.

A checkpoint's summary is agent-reported prose. Test results remain
telemetry measurements (such as `tests.passed`) with their source and
quality. `work continue` shows a successor exactly those measurements, so a
summary is never mistaken for evidence.
