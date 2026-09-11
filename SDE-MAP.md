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
| PostgreSQL catalog introspection | Live catalog into a `SchemaSnapshot`, with per-category completeness | `src/Strata.Host.Postgres/` | not needed — see below | Tier 4. Only project that may reference `Npgsql`. Read-only |
| Targeted retrieval | Assembles one compact answer plus its analysis scope and caveats | `src/Strata.Application/` | `src/Strata.Application/manifest.md` | Tier 3. Composes; holds no semantic authority |
| Corpus files | Reads `.sql` from a named directory into corpus sources | `src/Strata.Host.Files/` | not needed — see below | Tier 4. Only project that reads the filesystem for corpus input |
| CLI | Composition root: wires adapters to the application tier | `src/Strata.Cli/` | not needed — see below | Tier 4. `strata <command> --connection ... [--corpus ...]` |

## Repository-wide composition

- Composition/root entry point: `src/Strata.Cli/Program.fs`
- Shared contracts: `src/Strata.Analysis/StatementReferences.fs` is the
  dialect-adapter contract; `src/Strata.Analysis/DialectPort.fs` is the parser
  PORT every dialect adapter implements. Both live in Tier 2 because Tier 2 and
  Tier 3 depend on them; implementations live in Tier 4.
- Architecture checks: `scripts/check-semantic-architecture.sh`
- Boundary checks: `tests/Strata.Tests/WireTests.fs` asserts the hand-written
  wire vocabulary exactly, so a renamed tag fails a test rather than silently
  changing Strata's output contract.
- Build: `dotnet build Strata.sln`
- Tests: `dotnet test tests/Strata.Tests/Strata.Tests.fsproj`
- Live integration tests: set `STRATA_TEST_PG` to a PostgreSQL connection string;
  without it those tests **skip** rather than pass silently.

## Areas without separate manifests

| Area | Reason a separate manifest is not needed |
|---|---|
| `src/Strata.Host.PgParser/` | Two files with one responsibility — translate a libpg_query parse tree into `StatementExtraction`. Ownership is obvious from `DialectParser.fs`, which declares the whole inbound contract. Revisit when a second dialect adapter exists. |
| `src/Strata.Host.Postgres/` | Two files with one responsibility — read the catalog into a snapshot. All SQL is isolated in `CatalogQueries.fs` so it can be audited without reading F#. |
| `src/Strata.Host.Files/` | One module reading `.sql` from a named directory. Its whole contract is `FileCorpus.read`. |
| `src/Strata.Cli/` | One file, composition only. It holds no semantic decisions — argument parsing plus calls into Tier 3. |
| `tests/Strata.Tests/` | Test project; ownership follows the area each file tests, named in the file header. |
| `scripts/` | One script, self-describing, referenced from this map and from the architecture decision. |
