# Runtime-free fallback reconciliation

Use this path only when the repository's Praxis executable cannot run. The JSON envelope is untrusted proposed input; it does not authorize direct edits to `.ros` state and it does not relax work, evidence, identity, provenance, telemetry, or transition rules.

## Agent procedure

1. Create or use the work-item branch. Its exact name must equal `workItem`.
2. Record the full 40-character base commit observed before the proposed work.
3. Use a globally unique, stable `transactionId`. Reuse it for every retry.
4. Record ordered timeline/request pairs. Supported request types are `work.start` (or `work.begin`), `work.block`, `work.resume`, and `work.complete`.
5. Include `praxisInstanceId` only when it is actually available. Do not invent provider, model, runtime, session, token, or cost data.
6. Put the envelope under `.praxis/outbox/events/` for CI discovery, or pass its path directly to `./praxis reconcile --envelope FILE` once Praxis is available.

Minimal start envelope:

```json
{
  "schemaVersion": "1.0",
  "transactionId": "tx-WI-1234-001",
  "workItem": "WI-1234",
  "branch": "WI-1234",
  "baseCommit": "0123456789abcdef0123456789abcdef01234567",
  "agent": {
    "kind": "agent",
    "id": "provider/runtime",
    "provider": "provider",
    "model": "unknown",
    "runtime": "runtime"
  },
  "timeline": [
    { "sequence": 1, "timestamp": "2026-09-27T18:00:00Z", "action": "start" }
  ],
  "requests": [
    { "type": "work.start", "occurredAt": "2026-09-27T18:00:00Z", "workType": "feature" }
  ]
}
```

Replace every example identity, commit, ID, and timestamp with an observation from the actual run. `timeline` and `requests` have the same length and order. A completion request carries its evidence explicitly:

```json
{
  "type": "work.complete",
  "occurredAt": "2026-09-27T19:00:00Z",
  "evidence": [
    { "type": "implementation", "path": "src/feature.fs" },
    { "type": "tests", "path": "tests/FeatureTests.fs" }
  ]
}
```

The optional `execution.steps` shape is defined by [`protocol/praxis-envelope-v1.schema.json`](https://github.com/kemiller2002/praxis/blob/v3.7.2/protocol/praxis-envelope-v1.schema.json). Step names/descriptions, nesting, transitions, evidence references, measurement availability, and sanitized raw provider objects are preserved. Normalized `metricId` values and units must exist in the receiving repository's metric registry; an unmapped provider value belongs only in `rawTelemetry`. Registry aggregation is retained, so cumulative session counters are never rewritten as additive step deltas. A numeric zero is a value. `unavailable`, `unsupported`, and `unknown` measurements omit `value`; they are never encoded as zero. Calculated cost requires versioned pricing provenance. Estimated values require explicit confidence and remain estimated rather than appearing provider-reported.

## Reconciliation and retry

```bash
./praxis reconcile --envelope .praxis/outbox/events/tx-WI-1234-001.json
./praxis inbox list
```

Praxis verifies the actual branch, base ancestry, instance identity, request/timeline order, legal transitions, evidence paths, step lifecycle, measurement semantics, execution identity, and raw-telemetry retention limits. It renders all requests in an isolated staging root before canonical mutation. A completion envelope that supplies an execution must supply only terminal steps. A fallback completion also fails closed when the existing work item has a linked active native execution; finalize that execution through the native runtime before reconciling completion, so fallback input cannot silently abandon its telemetry.

Accepted context, events, optional execution telemetry, completion queue projection, and receipt writes are journaled, committed as an exact path set, and checkpointed by `praxis-reconcile/<transaction-id>`. The accepted input is removed only after that tag exists. The applied receipt lives under `.praxis/reconciled/`. A retry first resumes any pending journal; it does not re-run already-rendered transitions. A transaction is considered applied only when both its applied receipt and checkpoint tag exist and the receipt hash agrees with the checkpointed receipt.

Rejected envelopes are retained under `.praxis/rejected/` with machine-readable finding codes and produce no partial canonical state. Exit codes are: `0` applied/already applied, `1` pending recovery, `2` rejected input.

Run one reconciler per checkout. Independently installed Praxis instances remain local authorities; central publication is optional, and an unavailable registry never blocks reconciliation.
