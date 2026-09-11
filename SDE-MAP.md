# Repository Semantic Map

Routing table for Strata. It points to authority; it does not restate rules.
Required by `.sde/method/FEATURE-MANIFESTS.md` for a nontrivial adopting
repository.

Architecture authority: `research/decisions/DF-STRATA-2026-D3F8--four-tier-fsharp-architecture.md`
and `.sde/architecture/FOUR-TIER-ARCHITECTURE.md`.

| Semantic area / feature | Purpose | Location | Manifest | Notes |
|---|---|---|---|---|
| Semantic model | What can be true about a database: identity, evidence, resolution state, schema objects, analysis scope | `src/Strata.Semantic/` | `src/Strata.Semantic/manifest.md` | Tier 1. References only `FSharp.Core` |
| SQL analysis | Scope resolution and reference extraction over a dialect-neutral parse result | `src/Strata.Analysis/` | `src/Strata.Analysis/manifest.md` | Tier 2. Carries `RK-001`/`RK-002` |
| PostgreSQL parser adapter | libpg_query parse tree into Strata's extraction shape | `src/Strata.Host.PgParser/` | not needed — see below | Tier 4. Only project that may reference `pgsqlparser` |

## Repository-wide composition

- Composition/root entry point: none yet — no CLI or application host exists.
  `Strata.Application` (Tier 3) and `Strata.Cli` (Tier 4) arrive with WI-0013.
- Shared contracts: `src/Strata.Analysis/StatementReferences.fs` is the
  dialect-adapter contract; `src/Strata.Host.PgParser/DialectParser.fs` is the
  parser interface every dialect adapter implements.
- Architecture checks: `scripts/check-semantic-architecture.sh`
- Boundary checks: none yet — the wire contract (WI-0006) does not exist, so
  there is nothing to check agreement against. Stated rather than omitted.
- Build: `dotnet build Strata.sln`
- Tests: `dotnet test tests/Strata.Tests/Strata.Tests.fsproj`

## Areas without separate manifests

| Area | Reason a separate manifest is not needed |
|---|---|
| `src/Strata.Host.PgParser/` | Two files with one responsibility — translate a libpg_query parse tree into `StatementExtraction`. Ownership is obvious from `DialectParser.fs`, which declares the whole inbound contract. Revisit when a second dialect adapter exists. |
| `tests/Strata.Tests/` | Test project; ownership follows the area each file tests, named in the file header. |
| `scripts/` | One script, self-describing, referenced from this map and from the architecture decision. |
