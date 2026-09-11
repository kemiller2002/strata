# Feature Manifest — Corpus pipeline and targeted retrieval (Tier 3)

Routes to authority. Does not restate rules.

## Purpose

Assembles one compact answer to one question, together with the analysis scope
that bounds it. Implements ER-017 / D-019 (retrieval, not dumping) and PR-021
(every claim bounded by what was analysed).

## Ownership

- State: `Retrieval.fs` (`Query`, `Answer`); `CorpusPipeline.fs`
  (`CorpusAnalysis`).
- Transitions / derivations: `Retrieval.answer`, `Retrieval.toJson`,
  `Retrieval.toText`; `CorpusPipeline.analyse`, `CorpusPipeline.buildGraph`,
  `CorpusPipeline.toScope`.
- Invariants and guards: `caveatsFor` derives caveats from `Scope` rather than
  from call sites, so a new gap in scope reaches every answer automatically. An
  empty `Readers`/`Writers`/`Relationships` result carries an explicit
  "not an absence claim" caveat whenever the scope does not support one
  (§144.11). `toText` always prints caveats last.
- Capabilities / authority: **none, deliberately.** This tier must not become a
  second semantic authority. Certainty, resolution state and edge kind are all
  decided in Tier 1/2 and only rendered here.
- Important effects: none — no I/O. The snapshot and graph are passed in.

## Interfaces

- Inbound: `Retrieval.answer (snapshot) (graph) (scope) (query)`;
  `CorpusPipeline.analyse (parser) (snapshot) (searchPath) (sources)` — SQL
  arrives as `(origin, text)` pairs, never as a path, so this tier performs no
  I/O and the §8 rule against scraping stays enforceable by the caller.
- Outbound: `Json` (machine-readable, PR-017) and plain text (human-readable).

## Tests and verification

- Local behavior tests: `tests/Strata.Tests/RetrievalTests.fs`,
  `tests/Strata.Tests/CorpusPipelineTests.fs` (drives the real parser over real
  SQL text).
- Boundary/contract tests: `tests/Strata.Tests/WireTests.fs` owns the wire
  vocabulary this tier emits.
- Integration/live verification: the CLI was run against a live PostgreSQL
  16.15 instance for `inspect`, `relationships`, `path`, `readers` and a
  missing object. Not yet automated — there is no CLI-level test harness.

## Dependencies

- Allowed direct dependencies: `Strata.Semantic`, `Strata.Analysis`.
- Required composition context: `src/Strata.Cli/Program.fs`.

## Modification boundaries

- Normal: `src/Strata.Application/**`
- Escalation required: adding a host adapter reference or any `PackageReference`
  — the architecture check fails on both; and adding any decision about
  certainty or legality here, which would contradict `DF-STRATA-2026-D3F8`.

## Local agent instructions

- none — repository-level `AGENTS.md` governs.

## Maintenance

- Owner: Strata
- Last checked against implementation: 2026-09-11
- Known gaps: column impact covers the analysed corpus only — a `SELECT *`
  reader is attributed to every column and flagged `ViaWildcard`, but a column
  referenced only inside dynamic SQL is invisible and shows as an extraction
  gap rather than a dependency. `Path` ranking (§9) is shortest-path only; it does not yet rank by
  evidence strength, cardinality or deprecation. `JoinPredicate.QueryLevel` is
  always 0, so a join inside a subquery is not distinguished from one at the top
  level. Join detection covers equality predicates only — `BETWEEN`, `IN
  (SELECT ...)` and range joins are not relationship evidence yet, and their
  absence is not currently reported as a gap.
