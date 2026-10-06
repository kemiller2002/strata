---
id: DF-STRATA-FND-2026-0001
title: Forma, Folio and Limen are not applicable to Strata, a command-line tool with no browser or document surface; Aegis applies and is enforced at the CLI boundary
status: accepted
decision_type: applicability
created: 2026-10-06
updated: 2026-10-06
created_by_agent: claude
confidence: high
supersedes: []
superseded_by: []
evidence: []
related_documents:
  - .echelon/foundations.json
  - aegis-boundaries.json
  - limen.config.json
  - docs/strata/SHARED-APPLICATION-FOUNDATIONS.md
  - docs/strata/REQUIREMENTS-ANALYSIS.md
  - input-documents/strata-pre-requirements-design-notebook.txt
  - src/Strata.Cli/Boundary.fs
  - tests/Strata.Tests/BoundaryTests.fs
  - tests/Strata.Tests/FoundationsConformanceTests.fs
  - .github/workflows/echelon-foundations.yml
tags: [governance, foundations, aegis, forma, folio, limen, applicability]
---

# DF-STRATA-FND-2026-0001: Forma, Folio and Limen are not applicable to Strata

- **Date:** 2026-10-06
- **Status:** accepted
- **Decision type:** applicability declaration, with restoration triggers
- **Work item:** `FOUNDATIONS-PARITY`
- **Authority:** repository owner instruction, 2026-10-06: build all apps the
  same way. This record settles which of the Echelon foundations govern
  Strata at all.

## Context

`.echelon/foundations.json` has declared Forma, Folio and Limen
`required: false` since the foundations baseline was installed, and no
decision record said why. The shared foundations requirement this repository
adopted (`docs/strata/SHARED-APPLICATION-FOUNDATIONS.md` §1) allows that only
with a reason: "An implementation MAY mark one of Aegis, Forma, or Folio not
applicable only when the capability is genuinely outside that feature's
boundary. The reason MUST be explicit and reviewable. Silence is not an
exception." The Praxis foundations schema has no reason field, so the reason
belongs here, as it does for the other applications' `DF-<REPO>-FND-*`
records.

Signal, Chrona and Summa each had a `-FND-2026-0001` record that declared the
browser foundations *not yet* applicable, and each was superseded by a `0002`
record when the owner had those applications built on the full stack. Those
three had, or were chartered to have, a browser surface: Signal had a planned
`src/Echelon.Signal.Browser`, Chrona a browser kernel slice, and Summa its hub
and backlog pages. The question for Strata is therefore not "should it match
them" but the one that came first for them: **does Strata have, or do its
charter or requirements call for, a user-facing web or browser surface?**

## Evidence: Strata is a command-line tool, and nothing calls for a browser

### What the repository says Strata is

- **README.** "Declarative schema management for PostgreSQL." Install needs
  ".NET 8 and, for anything touching a database, PostgreSQL 16". Every
  documented use is a shell command (`strata compile`, `deploy`, `drift`,
  `plan`, `check`, `validate`, `inspect`, `keygen`, `sign`), run as
  `dotnet run --project src/Strata.Cli`. Output is terminal text or `--json`.
- **Requirements** (`docs/strata/REQUIREMENTS-ANALYSIS.md`). The 25 product
  requirements PR-001 to PR-025 cover parsing, introspection, the semantic
  model, graphs, effects, retrieval, validation, policy, diff, drift and
  deployment. The only output requirement is PR-017, "Emit machine-readable
  (JSON) and human-readable output". No requirement names a web UI, a browser,
  a page, a printable report or a PDF.
- **The source of those requirements**
  (`input-documents/strata-pre-requirements-design-notebook.txt`).
  - §14, *Agent interface*: the potential commands are all `strata …` CLI
    commands; "Human-readable equivalent: concise terminal output."
  - §41, *Machine-readable output*: "Human terminal output is a projection of
    the structured model. Agents should consume stable JSON rather than scrape
    human prose."
  - The deferral list puts "IDE UI" and "sophisticated visualization" under
    "Delay until the semantic model proves itself", and "dependency
    visualization" among later integrations. NG-004 is "Creating a visual query
    builder".
- **Charter** (`PROJECT-CHARTER.md`). Still the starter template: purpose,
  users and first outcome are "not yet established", and the text about
  "communication problems" is inherited from the profile. It names no
  surface of any kind, so it neither calls for a browser nor rules one out.
  The requirements analysis is the operative scope.
- **Foundations requirement itself.** §3 scopes Forma: "required only for
  Strata interactive web UI surfaces when such a surface is implemented." §4
  scopes Folio: "required only for printable/PDF/paginated schema reports,
  deployment plans, analysis reports, or other document outputs when such
  outputs are implemented", and §4.10: "Folio need not be installed solely for
  symmetry."

### What the code is

- Seven F# projects: `Strata.Semantic`, `Strata.Analysis`,
  `Strata.Application`, three Tier 4 hosts (`Strata.Host.Postgres`,
  `Strata.Host.PgParser`, `Strata.Host.Files`) and one executable,
  `Strata.Cli` (assembly `strata`). Layering is `DF-STRATA-2026-D3F8`.
- Every user-facing output is `printfn`/`eprintfn` to the terminal, a JSON
  document, or a file Strata writes for another Strata command to read (the
  compiled `.strata` artifact, `DF-STRATA-2026-2F6B`; signed key pairs). None
  is a paginated, printable or preview document.
- There is no `package.json`, no `.html`, `.css`, `.js` or `.ts` source, no
  `<ef-*>` markup, no WebAssembly project and no browser kernel anywhere under
  `src/` or `tests/`. `tests/Strata.Tests/FoundationsConformanceTests.fs`
  asserts this, so the record cannot silently go stale.

### Limen has already reached the same conclusion

`limen.config.json` declares `boundary.notApplicable` ("Strata is a
command-line schema-management tool for PostgreSQL … no browser application
and no WASM engine"), and `limen verify --strict` (Limen 0.7.0) reports
`not-applicable` and exits 0 (commit `fc3c713`, `LIMEN-0-7-0`). Limen is a
browser/application interaction boundary between an F# engine and a browser
kernel; Strata has neither side of it. The Limen installation and its verify
workflow stay in place so that the not-applicable claim is re-checked on
every change.

## Decision

**Strata is a command-line tool, not a browser application.** No UI is
invented to make the foundations symmetrical; that would be a product surface
no requirement asks for, built to satisfy a dependency check, and the
verifier's `used` rule exists to reject exactly that.

1. **Forma: not applicable** (`required: false`). There is no interactive
   browser surface.
2. **Folio: not applicable** (`required: false`). There is no printable,
   paginated or PDF output.
3. **Limen: not applicable** (`required: false`). There is no engine/kernel
   boundary; Limen's own verifier agrees.
4. **Aegis: applicable and required** (`required: true`). Strata is .NET and
   owns real operational boundaries: PostgreSQL (catalog reads, shadow
   normalisation, transactional deployment), the filesystem (project, corpus,
   artifacts, keys) and the process. Aegis was referenced and used at the CLI
   entry point, but with gaps this change closes:
   - Nine command handlers caught every exception themselves and printed the
     raw `ex.Message` with exit 2 (exit 1 for queries), so those failures never
     reached Aegis and the operator saw raw technology text. They now run
     through `Strata.Cli.Boundary.attempt`, which records the fault and prints
     only a safe message and a reference. The exit codes, which are part of
     each command's contract, are unchanged.
   - The PostgreSQL and filesystem boundaries in `aegis-boundaries.json` had
     no codes and were `guarded: false`. Escaped failures are now classified
     at the CLI composition root into `STRATA.POSTGRES.FAILURE` (any
     `DbException`) and `STRATA.FILES.FAILURE` (`IOException`,
     `UnauthorizedAccessException`), with `STRATA.CLI.UNEXPECTED` for the
     rest. The Cli tests on `DbException`, not Npgsql's type, because only
     `Strata.Host.Postgres` may reference Npgsql (`DF-STRATA-2026-E8C1`).
   - Aegis was configured `Detached`, so a fault's record could lose the race
     with process exit. A command-line process awaits delivery (`Blocking`).
   - A malformed connection string reached Npgsql as an `ArgumentException`,
     which Aegis treats as a programming defect and re-raises. It is the
     operator's input, so `Strata.Host.Postgres.ConnectionString.validate`
     now refuses it as a typed outcome (exit 2) before any command runs, and
     never echoes the string, which carries credentials.
   - There were no Aegis tests. `BoundaryTests.fs` drives the boundary with a
     deterministic collector sink; `FoundationsConformanceTests.fs` fails if
     Aegis stops being referenced, pinned, configured, used by the commands,
     or if the declared codes and the codes in `Boundary.fs` drift apart.

`.echelon/foundations.json` keeps the restoration baselines current: Forma and
Folio at their v0.3.0 releases (the versions Signal, Chrona and Summa pin),
Limen at 0.7.0. The stale Folio `sourceCommit` is dropped because Folio
v0.3.0 is now a published release, which the shared requirement says to pin
instead (§1).

## Restoration triggers (each flips the capability to `required: true` in the same change)

1. **Forma and Limen:** the first interactive browser surface: a web UI, a
   hosted dashboard, a dependency or drift visualiser, an IDE webview. It is
   built the way Summa, Signal and Chrona are: a pure F# Limen engine, an F#
   application tier behind the Aegis boundary, a C# WebAssembly shim, the
   Limen browser kernel, and Forma `all.css` with `<ef-*>` markup, pinned to
   the then-current releases.
2. **Folio:** the first printable, paginated or PDF output, such as a printable
   deployment plan, drift report or schema report. It consumes the pinned
   `@echelon-foundry/print-components` release.

`FoundationsConformanceTests` enforces both triggers: it fails if any
`package.json`, `.html`, `.css`, `.js`, `.ts` or `.razor` source, or `<ef-*>`
markup, appears under `src/` or at the repository root while Forma, Folio or
Limen is still declared not applicable. Such a change must flip the
declaration and adopt the stack, or supersede this record.

## Alternatives considered

- **Build a web UI so Strata matches Summa, Signal and Chrona.** Rejected.
  Those applications adopted the stack for a browser surface they had or were
  chartered to have. Strata has none, its requirements specify terminal and
  JSON output, and its design notebook defers UI and visualisation until the
  semantic model proves itself. A UI built only to make Forma, Folio and Limen
  report PASS would be a fabricated surface, which the shared requirement (§1,
  "merely listing a shared dependency is not sufficient") and the verifier's
  `used` check both exist to prevent.
- **Install the npm packages without a surface.** Rejected for the same
  reason: an unused dependency, and the verifier would rightly fail `used`.
- **Leave the declaration unexplained.** Rejected: §1 says silence is not an
  exception.

## Consequences

- `praxis foundations verify` (pinned `bd3d1469`) reports Aegis, Ordo and
  Praxis `PASS` and Forma, Folio and Limen `N/A`, and exits 0.
- `limen verify --strict` keeps reporting `not-applicable`.
- An operator sees `error: Strata could not … [AG-xxxxx]` instead of a raw
  exception message; the exception's type and redacted message are in the
  Aegis fault record on standard error, findable by that reference.

## Revisit trigger

Either restoration trigger above; a charter update that names a browser or
document surface; a requirement that adds one; or a Praxis foundations schema
change that adds an explicit not-applicable state with a reason.
