# External Work-System Adapter Contract 1.0

## Boundary and authority

ROS defines this transport-neutral contract but does not own project-management truth. The external system owns work items and their operational state. A repository owns implementation and evidence. ROS owns validation of repository evidence and the legal protocol connecting them. An inconsistency is returned as a conflict; neither side silently overwrites the other.

## Operations

Version 1.0 defines three normalized operations:

- `getWorkItem` reads external work-item identity and state.
- `transitionWorkItem` requests a state transition with an optional expected-state precondition.
- `publishRepositoryEvent` publishes a validated, versioned repository event or evidence reference.

`listWorkItems` and product-specific queries remain deferred until a real consumer demonstrates a stable need.

Requests and results conform to `schemas/work-adapter-request.schema.json` and `schemas/work-adapter-result.schema.json`. Every request carries a protocol version, globally durable request ID, repository identity, authenticated principal identity, and operation-specific fields. Authorization is represented as scopes; production adapters must derive trusted scopes from credentials rather than accepting caller assertions.

## Outcomes and delivery semantics

Every result is exactly one of:

- `success`: the adapter confirmed the requested read or effect.
- `failure`: the adapter confirmed rejection or non-application, with a stable error code.
- `unknown`: the adapter cannot determine whether the remote effect occurred.

An `unknown` outcome must never be promoted to success. Callers retry with the same request ID. Adapters persist the first result for a request ID, and semantic events carry their own event IDs, preventing duplicate transitions and publications. A new request ID represents a new requested operation.

Protocol-version mismatch, unknown repositories, missing authorization, missing work items, expected-state conflicts, and invalid events are explicit failures. Authentication mechanism, credential storage, transport encryption, rate limiting, and retry timing belong to the deployment adapter and are not encoded as repository secrets.

## Conformance adapter

The dependency-free file adapter exercises the contract without choosing a project-management vendor:

```bash
./ros adapter call \
  --store tests/fixtures/work-store.json \
  --request tests/fixtures/transition-request.json
```

It returns exit code `0` for success, `1` for failure, and `2` for unknown. The store is a test double, not a production project-management datastore. Its supported fault-injection field, `simulateOutcome: unknown`, exists only to verify that callers preserve uncertainty.

## Production adapter requirements

A production adapter must validate repository identity against its authenticated principal; authenticate outside request content; derive authorization scopes; preserve request and event idempotency; negotiate protocol versions; use bounded retries; retain `unknown` outcomes; avoid logging credentials or sensitive evidence; and pass the shared conformance suite before adoption.
