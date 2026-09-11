---
id: EV-STRATA-2026-A7C3
title: Indexing cost was 85% reflective string formatting of gap messages nothing reads, not parsing and not the absence of a cache — 34.9s to 8.0s with byte-identical output
research_area: strata-scale
evidence_type: test-result
source_title: Strata indexing-cost profile and single-pass extraction
source_author: claude
source_uri: 3,000-file / 10,521-statement synthetic corpus; PostgreSQL 16.15; .NET 8.0.131 Release
source_date: 2026-09-11
retrieved: 2026-09-11
created_by_agent: claude
confidence: high
supports: []
contradicts: []
related_theories: []
tags: [strata, performance, profiling, caching, correction]
---

# Evidence Record

## Evidence summary

The indexing cost that `EV-STRATA-2026-C9A4` and `EV-STRATA-2026-D7B2` both
report was attributed, in this session and in those records' framing, to two
causes: that Strata re-indexes on every query because **no cache exists**, and
that the **parser** is inherently expensive. Profiling supports neither.

Measured on a 3,000-file, 10,521-statement corpus against a live PostgreSQL
16.15 catalog, one `strata readers crm.t_00 --json --brief` invocation:

| | baseline `bb0debf` | after | factor |
|---|---|---|---|
| end-to-end, warm, Release | **34.9 s** | **8.0 s** | **4.4x** |

The answer is byte-identical before and after (`md5 bde7ef4e…`).

## Where the time actually went

Per-component, over the same 10,521 statements:

| component | seconds | share of `analyse` |
|---|---|---|
| `sprintf "%A"` over gap messages | **18.6** | **~83 %** |
| parse (`ParseScript`, whole corpus) | 2.6 | 12 % |
| extraction (six reflective walks) | ~9.0 | — |
| `columnDependencies` | 1.9 | 9 % |
| catalog introspection | 0.7 | — |
| `Fingerprint` per statement | 0.17 | — |
| `sha256Hex` per statement | 0.05 | — |
| `ScopeChain.ofExtraction` x3 | 0.02 | — |
| `dependencyEdgeCandidates` | 0.02 | — |
| `Effect.classify` | 0.03 | — |
| read 3,000 files from disk | 0.08 | — |

`CorpusPipeline.analyse` built **58,578 gap-message strings** per run with
`sprintf "%A"`, F#'s reflection-based structured formatter.

**No shipping code path reads those strings.** `CorpusAnalysis.Gaps` is
consumed by nothing in `Strata.Cli`; the only gap information that reaches
output is a *count*, via `Retrieval` rendering `scope.Corpus.ExtractionGaps`.
That the output is byte-identical after the change is itself the proof: 18.6
seconds per query went into text that was computed and discarded.

This also violated a rule the codebase already states. `Resolution.tag` carries
the comment *"Hand-written per Boundary Preservation: the wire vocabulary must
not depend on incidental F# union-case reflection."* `sprintf "%A"` on a
`ResolutionGap` is exactly that dependency, and the cost was the symptom.

## Changes made

1. `ResolutionGap.describe` (Tier 1) and `EffectKind.describe` (Tier 2),
   hand-written, replacing the two `%A` call sites. These were the only `%A`
   uses in `src/`.
2. `PgParserAdapter` walks the parse tree **once** instead of six times. Six
   independent `descend` passes — CTE names, relations, columns, join
   predicates, unmodelled shapes, dynamic SQL — became one `foldTree` threading
   an immutable `Gathered` state. Extraction: 0.851 -> 0.145 ms/statement,
   **5.9x**; per protobuf node 24.1 -> 4.1 us.
3. Release is now the default configuration for project builds, and a Debug
   `strata` binary announces itself on stderr (see "Build configuration").

## Correctness of the extraction rewrite

Behaviour-preservation was verified by differential test, not by unit tests
alone. Both adapter versions were built in Release and run over the same input,
dumping the **complete `StatementExtraction`** — every field, every statement:

- 62 repository fixtures (`examples/corpus`, `examples/gate`, `examples/seams`):
  **byte-identical**, `md5 a32d1b60…`
- A purpose-built 61-statement branch-covering corpus: **byte-identical**,
  `md5 2d623816…`

The first corpus alone was **not** an adequate gate: it never exercised dynamic
SQL, temporary relations, BETWEEN/EXISTS/ALL shapes, or six of the eight ALTER
subtypes. The second was written after checking that coverage and hits all of
them, plus multi-CTE ordering, recursive and nested CTEs, quoted identifiers,
qualified operators, and three-part names.

Full suite: 179/179 passing with the live catalog connected; architecture check
passing.

## Build configuration

The timings in `EV-STRATA-2026-C9A4` and `EV-STRATA-2026-D7B2` were taken from
**Debug** builds, worth roughly 1.6x. Nothing in those records says so, because
nothing in the invocation said so — `dotnet build` defaults to Debug silently.
Fixed in two places: `Directory.Build.props` defaults `Configuration` to Release
for project builds (a solution build passes `Configuration=Debug` as a global
property that no props file can override, hence `scripts/build.sh`), and the CLI
prints a Debug notice to stderr so a timing cannot be taken from an unoptimised
binary unremarked.

## What this does NOT establish

- **It does not correct the 175 s / 247 s figures.** This is a different corpus,
  generated for this measurement (seed 20260911, 3,000 files, 10,521
  statements, 12 MB). The corpora behind those records no longer exist in this
  container. What carries over is that both defects were present in that code,
  so those figures are inflated by an unknown amount — not that they were 34.9 s.
- It says nothing about correctness or value; only cost.
- The remaining 8.0 s has not been reduced further.

## The caching decision

**A cache is not warranted yet, and the profile is why.**

Before: extraction and gap formatting were 79% of `analyse`, and a cache in
front of that would have preserved both defects behind a hit rate while adding
invalidation risk. Strata's guarantee is that it never reports a stale
dependency set as current; a cache keyed on file content still has the catalog
as a second input, where a `CREATE TABLE` invalidates resolution with nothing
on disk changing to signal it.

After: `analyse` is 5.2 s, of which parse is 2.6 s — now the single largest
term. Parsing **is** the one safely cacheable step, because the parse of a given
text is a pure function of the text and the parser version, with no catalog
input and therefore no staleness question. If indexing cost needs to fall
further, a content-hash-keyed parse cache is the next move, and it is a
different and much smaller proposition than caching resolved analysis.

## Defect found and deliberately NOT fixed here

`OPERATOR(pg_catalog.=)` is reported **both** as a join predicate (correct) and
as `"column-to-column predicate with operator 'pg_catalog' is not modelled as
join evidence"` (wrong). The join path tests every name part for `=`
(`Seq.exists`); the unmodelled path takes the first part (`Seq.tryPick`) and
gets the schema. Pre-existing, preserved byte-for-byte so the differential test
stays a clean behaviour-preservation proof. It errs toward a spurious analysis
gap rather than a missed one, so it over-warns rather than under-warns.

## Reproduction

```
scripts/build.sh
STRATA_TEST_PG=... dotnet test Strata.sln -c Release
STRATA_PG=... STRATA_CORPUS=<corpus> \
  src/Strata.Cli/bin/Release/net8.0/strata readers crm.t_00 --json --brief
```
