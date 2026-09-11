---
id: EV-STRATA-2026-7A31
title: libpg_query-based .NET parser viability for Strata (Spike A)
research_area: strata-parsing
evidence_type: test-result
source_title: Strata Spike A — parser viability probe
source_author: claude
source_uri: scratchpad spike, corpus reproduced in this record
source_date: 2026-09-11
retrieved: 2026-09-11
created_by_agent: claude
confidence: high
supports: []
contradicts: []
related_theories: []
tags: [strata, parser, libpg_query, spike-a]
---

# Evidence Record

## Evidence summary

`pgsqlparser` 1.0.0 (MIT, .NET wrapper over libpg_query) parses a 36-statement
representative PostgreSQL corpus from F# with no wrapper-level failures, parses
PL/pgSQL, produces stable fingerprints, and splits multi-statement input. It is
viable as Strata's PostgreSQL parser adapter. Two corpus entries failed to
parse; both were deliberately invalid SQL and failed with accurate, located
error messages, which is the required behaviour.

## Exact claim supported or contradicted

Supports D-003 ("use a real PostgreSQL parser; do not write our own") and
D-004 ("libpg_query-based .NET wrapper is the leading parser direction pending
a technical spike"). Resolves Q-001 for PostgreSQL to a specific package,
subject to the limitations below.

## Source provenance

| Item | Value |
|---|---|
| Package | `pgsqlparser` 1.0.0 |
| License | MIT (wrapper); libpg_query/PostgreSQL licensing applies to the native portion |
| Author | Babu Annamalai |
| Project | https://github.com/mysticmind/pgsqlparser-dotnet |
| Dependency | `Google.Protobuf` 3.33.0 |
| Target frameworks | net8.0, net9.0 |
| Bundled natives | linux-x64, osx-arm64, osx-x64, win-x64 |
| Reported parser version | PostgreSQL 17.5 (`PgVersionNum` = 170005) |
| Host used | .NET SDK 8.0.131, linux-x64 |

Alternative evaluated: `PostgresQuery` 0.1.4 (BSD-3-Clause, Altan Birler,
https://github.com/hbirler/pgquery-sharp). It restores and ships a **wider**
native matrix — linux-arm64, linux-musl-x64, linux-musl-arm64, win-arm64 in
addition to the four above. It was not selected as primary, but it is the
documented fallback and the better option if Strata must run on arm64 or musl.

A third NuGet package, `PgQuery` 0.1.6, was evaluated and **rejected**: despite
the name it is an Npgsql-based query builder, not a libpg_query wrapper.

## Relevant excerpt or data

Corpus result: **34 parsed, 2 failed, 36 total.**

Parsed successfully: simple SELECT, `SELECT *`, INSERT, bounded UPDATE,
unbounded UPDATE, unbounded DELETE, inner join, multi-join with LEFT JOIN, CTE,
recursive CTE, aggregate with HAVING, window function, subquery, LATERAL, array
literals and containment, JSON `->>`/`#>`/`@>`, RETURNING, upsert
(`ON CONFLICT DO UPDATE`), `CREATE TABLE`, `ALTER TABLE ADD COLUMN`,
`ALTER TABLE DROP COLUMN`, `ADD CONSTRAINT ... FOREIGN KEY`,
`CREATE INDEX CONCURRENTLY ... WHERE`, `CREATE VIEW`,
`CREATE MATERIALIZED VIEW`, `CREATE FUNCTION` with dollar quoting,
`CREATE TRIGGER`, quoted mixed-case identifiers, schema-qualified names,
`TRUNCATE ... CASCADE`, `GRANT`, multi-statement transaction block,
set operations, casts.

Deliberate negative cases, both correctly rejected:

```
FAIL  syntax-error   cursor=13  msg=syntax error at or near "WHERE"
FAIL  gibberish      cursor=1   msg=syntax error at or near "this"
```

Other capability results:

| Capability | Result |
|---|---|
| PL/pgSQL parse (`ParsePlpgsql`) | success, including a nested `EXECUTE` of constructed SQL |
| Fingerprint stability across differing literals | true (`id = 1` and `id = 999` fingerprint identically) |
| Statement splitting (`SplitWithScanner`) | success, 3 of 3 statements |
| Multi-statement transaction block | reported as 3 statements |

API shape relevant to Strata's adapter boundary, from reflection:

```
PgSqlParser.Parser (all static):
  Parse(string, ParserOptions) -> Result<ParseResult>
  ParsePlpgsql(string)         -> Result<...>
  Fingerprint(string, ParserOptions)
  SplitWithScanner / SplitWithParser
  Normalize / NormalizeUtility
  Deparse(ParseResult)
  PgVersion / PgMajorVersion / PgVersionNum
PgSqlParser.Result<T>: IsSuccess, Value, Error
PgSqlParser.Error: Message, FuncName, FileName, LineNo, CursorPos, Context
```

## Interpretation

The wrapper returns an explicit `Result<T>` with a structured `Error` carrying
a cursor position, rather than signalling failure by exception. This matches
Strata's F# requirement for explicit `Result`/error types and no
exception-driven normal control flow, so the adapter boundary can stay thin:
the Tier 4 adapter maps `Result<ParseResult>` onto Strata's own parse-outcome
type without inventing an exception-translation layer.

`Fingerprint` is directly usable for the notebook's query-fingerprint concept
(§123) and for corpus deduplication. `SplitWithScanner` covers the statement
boundary requirement in §8 without Strata writing a SQL lexer.

## Limitations

1. **Parser version is PostgreSQL 17.5.** Strata targets one parser major at a
   time. A live database of a different major may accept or reject syntax this
   parser does not. This makes Q-002 and Q-026 live requirements, not
   theoretical ones, and it must surface as an explicit analysis state rather
   than silent acceptance.
2. **Native platform coverage of the selected package excludes arm64 and musl.**
   CI or deployment on those platforms requires the `PostgresQuery` fallback or
   a self-built native. Not yet tested on Windows or macOS from this session —
   only linux-x64 was executed.
3. Parse success proves syntactic acceptance only. It does not prove the target
   database has the referenced objects, extensions, operators, types or
   collations. See `EV-STRATA-2026-B9C4` for what the AST cannot resolve.
4. Only one wrapper was executed end to end. `PostgresQuery` was inspected for
   packaging, licence and native matrix but its API was not exercised.
5. NuGet's search index is blocked by this environment's egress policy, so
   package discovery was by exact id against the flat container. A better
   wrapper may exist that was not discoverable here.

## Counterevidence

None observed for the viability claim. The two parse failures were intended
negative controls and behaved correctly.

## Reproduction or verification notes

Create a net8.0 F# console project, `dotnet add package pgsqlparser --version
1.0.0`, call `Parser.Parse(sql, ParserOptions())` over the corpus listed above,
and assert 34 successes and 2 located failures. Verified 2026-09-11 on .NET SDK
8.0.131, linux-x64.
