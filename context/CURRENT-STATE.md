# Strata current state

## Repository status

Repository Operating System 2.0.1 and State-Directed Engineering 1.1.1 are
installed and verified. The Strata pre-requirements design notebook has been
analyzed into a requirements corpus; implementation has begun.

## Observed facts

- `EV-STRATA-2026-7A31`: `pgsqlparser` 1.0.0 parses 34 of 36 representative
  PostgreSQL statements from F#; the two failures were deliberate negative
  controls. PL/pgSQL parses. Parser targets PostgreSQL 17.5.
- `EV-STRATA-2026-B9C4`: a naive parse-tree walk produces a **false** dependency
  edge when a CTE name shadows a table name, and reports no column references
  for `SELECT *`. Scope resolution is Strata's responsibility.
- No live PostgreSQL server has been available in this session, so the
  live-binding half of Spike B is untested.
- ROS telemetry reports token and cost metrics as *unsupported* for this
  runtime; agent cost is unavailable rather than zero.

- `EV-STRATA-2026-D8E1`: six agent runs found **no correctness difference**
  between Strata retrieval and raw context (15/15 both), and Strata cost 8.7%
  more tokens at 4-table scale. A ceiling effect, not a refutation — but the
  agent thesis is not established, so per notebook §134 it must not be Strata's
  sole justification.

## Assumptions

- A catalog snapshot plus scope resolution resolves most references offline
  (`HY-STRATA-2026-4D92`, untested at scale).
- Deployment-safety value is independent of agent-leverage value
  (`HY-STRATA-2026-6F14`, untested).

## Active work

Slice S1: PostgreSQL semantic inspection — canonical semantic model and catalog
introspection, under the four-tier architecture of `DF-STRATA-2026-D3F8`.

- `EV-STRATA-2026-D7B2`: at 3,009 files the agent advantage **does not
  survive**. `grep` is ~16,000x faster and answered every seam question
  correctly. Strata has no corpus-wide seam query — `relationships` is
  per-object, so "where are the seams?" would need 200 invocations (~14 hours).

## Largest decision-relevant unknown

**The agent thesis is rejected** (`HY-STRATA-2026-2A6F`, `EV-STRATA-2026-E3D7`):
four regimes, thirteen agent runs, no correctness advantage anywhere. The
remaining justification for the program is `HY-STRATA-2026-6F14` — that
deployment-safety value stands independently — **which has never been tested**.
Testing it, or deciding the program on its absence, is now the only question
that matters.

Superseded framing: The crossover
question is now settled the wrong way: `EV-STRATA-2026-D7B2` shows that at the
scale where Strata was supposed to win, `grep` wins on both cost and
correctness. Two things must exist before the thesis can be retested — a cached
index (`Q-010`, `Q-011`, `Q-020`) and a corpus-wide seam query — and the retest
must use a corpus with unqualified names and dynamic SQL, where text search
degrades and a resolved graph should not.

Previously largest, still open: `Q-005`/`Q-027`, how much semantic binding
Strata must implement versus delegate to a live server.

## Baseline

Recorded and tagged before implementation:
`docs/baselines/BASELINE-20260911.md`, tag
`strata-implementation-baseline-20260911-135126`, HEAD `3f082b9`.
No Strata source code, build or tests existed at baseline.
