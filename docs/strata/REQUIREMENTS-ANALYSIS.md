# Strata requirements analysis

**Source corpus:** `input-documents/strata-pre-requirements-design-notebook.txt`
(4,057 lines, 144 sections, dated 2026-09-07, status *Exploratory /
pre-requirements*).

**Purpose:** normalize the notebook into requirements, rules, decisions,
hypotheses, risks, open questions and non-goals, then challenge the result. The
notebook is a source corpus, not a specification; §133 explicitly instructs
"Do not convert every idea here into a MUST."

**Status of this document:** analysis output. Authority for identifiers,
confidence and lifecycle is the ROS records this document points to.

## Method

Every substantive notebook item was classified into one of nine categories
(A–I). Items already carrying an identifier in the notebook keep it: `P-nnn`
principles, `C-nnn` capabilities, `D-nnn` decisions, `Q-nnn` open questions,
`U-nnn` user groups. New identifiers introduced here use `PR-` for product
requirements, `ER-` for engineering rules, `NFR-` for nonfunctional
requirements, `RK-` for risks and `NG-` for non-goals.

Two technical spikes were run **before** the classification was finalized, so
that the parser and binding decisions rest on evidence rather than on the
notebook's own expectation: `EV-STRATA-2026-7A31` (parser viability) and
`EV-STRATA-2026-B9C4` (parse versus semantic binding). The second materially
changed the plan; see "Findings that change the notebook's assumptions".

## Counts

| Category | Count |
|---|---:|
| A. Product requirements (`PR-`) | 24 |
| B. Engineering rules (`ER-`) | 20 |
| C. Nonfunctional requirements (`NFR-`) | 12 |
| D. Architecture decisions (`D-`) | 20 |
| E. Hypotheses / experiments (`HY-`/`EX-`) | 6 |
| F. Risks (`RK-`) | 18 |
| G. Open questions (`Q-`) | 30 |
| H. Deferred / later possibilities | 11 |
| I. Explicit non-goals (`NG-`) | 10 |

## A. Product requirements

Derived from notebook §4 capabilities `C-001`–`C-024`, narrowed by the Phase 3
challenge. "First useful version" means the smallest Strata that is useful even
if deployment execution never ships (§144.15).

| ID | Requirement | Source | In first useful version? |
|---|---|---|---|
| PR-001 | Parse PostgreSQL SQL through a real PostgreSQL parser | C-001, §5 | yes |
| PR-002 | Split multi-statement SQL into statements with source locations | §8 | yes |
| PR-003 | Parse PL/pgSQL routine bodies | §5.1 | yes |
| PR-004 | Introspect a live PostgreSQL database's schema objects | C-002, §6 | yes |
| PR-005 | Report introspection completeness per metadata category | §6 | yes |
| PR-006 | Represent database structure in a canonical Strata semantic model | C-003, §7 | yes |
| PR-007 | Serialize the semantic model deterministically | §31, NFR-001 | yes |
| PR-008 | Index a SQL corpus with provenance per unit | C-004, §8 | yes |
| PR-009 | Extract referenced objects, reads and writes from SQL | C-005, §8, §10 | yes |
| PR-010 | Resolve lexical scope (CTEs, aliases, subqueries) before emitting edges | EV-STRATA-2026-B9C4 | yes |
| PR-011 | Classify every reference by resolution state, never collapsing states | P-008, §12 | yes |
| PR-012 | Build a dependency graph from catalog and corpus evidence | C-006, §9 | yes |
| PR-013 | Build a relationship graph distinguishing declared from observed | C-007, §9 | yes |
| PR-014 | Attach provenance and evidence to every derived fact | P-007, §9 | yes |
| PR-015 | Classify statement effects by consequence, not statement type | C-008, §10 | yes |
| PR-016 | Answer targeted retrieval queries over the semantic model | C-010, §14 | yes |
| PR-017 | Emit machine-readable (JSON) and human-readable output | C-024, §14 | yes |
| PR-018 | Validate candidate SQL against the current schema | C-011, §12 | **yes — ACTIVE, slice V** |
| PR-019 | Evaluate consequence-based policy over classified effects | C-009, §11 | yes |
| PR-020 | Explain every finding: what, why, severity, certainty, next safe move | §130, P-020 | yes |
| PR-021 | Bound every impact claim by declared analysis scope | §129 | yes |
| PR-022 | Compare desired schema against actual schema | C-012, §17 | **ACTIVE, slice D** |
| PR-023 | Detect drift between environments | C-013, §27 | **ACTIVE, slice D** |
| PR-024 | Plan, execute and verify deployments | C-014/018/019, §18, §28 | **ACTIVE, slice X** |
| PR-025 | Read desired state from a project manifest and per-object SQL files | DF-STRATA-2026-B1E7, DF-STRATA-2026-A4D9 | **ACTIVE, slice P** |

Capabilities `C-015`–`C-023` map onto PR-022..PR-024 and the deferred list;
they are not separate first-version requirements.

## B. Engineering rules

Constraints on *how* Strata behaves. These are the architecture's guardrails and
most originate as notebook principles.

| ID | Rule | Source |
|---|---|---|
| ER-001 | Do not write a PostgreSQL parser | P-001, D-003 |
| ER-002 | SQL remains a first-class authoring language | P-002, D-007 |
| ER-003 | No mandatory meta-query language | P-003, D-008 |
| ER-004 | Constrain effects, not expressiveness | P-004, D-009 |
| ER-005 | Deterministic decisions belong to deterministic code | P-005 |
| ER-006 | AI may assist ambiguous intent but never decides destructive intent | P-006 |
| ER-007 | Every semantic fact carries provenance | P-007, D-017 |
| ER-008 | Unknown, unsupported, unparsed, ambiguous, inaccessible and absent are distinct states | P-008, D-018 |
| ER-009 | Fail closed when visibility is incomplete for high-risk operations | P-009 |
| ER-010 | Desired state alone does not authorize every transition | P-010, D-011 |
| ER-011 | Verification is part of deployment | P-011 |
| ER-012 | Distinguish reversible / compensatable / roll-forward-only / restore-required | P-012 |
| ER-013 | Dialect differences are first-class; no lossy generic abstraction | P-013 |
| ER-014 | The semantic model is smaller than the parser AST | P-014, D-005, D-006 |
| ER-015 | Reference data is database state with stricter ownership semantics | P-015, D-012, D-013 |
| ER-016 | Business data is never implicitly reference data | P-016 |
| ER-017 | Agent context is retrieved, not dumped | P-017, D-019 |
| ER-018 | A known relationship is not the only legitimate relationship | P-018 |
| ER-019 | The database remains the execution authority | P-019 |
| ER-020 | Strata's own decisions must be inspectable | P-020 |

Additional rules the notebook states as absolutes and this analysis keeps as
rules rather than requirements: never convert an observed relationship into a
declared FK automatically (§9); never execute a rename from similarity
inference (D-015); never delete unexpected reference rows by default (D-014);
never treat absence as intent to delete (§140); `EXPLAIN ANALYZE` executes the
query and is never a harmless validation step (§13).

## C. Nonfunctional requirements

| ID | Requirement | Source |
|---|---|---|
| NFR-001 | Deterministic output for identical input | §31, P-005 |
| NFR-002 | Reproducible analysis given a pinned parser and catalog snapshot | §31 |
| NFR-003 | Stable, versioned machine-readable output contract | §14, §128 |
| NFR-004 | Parser and PostgreSQL major versions pinned and reported | §5.1 |
| NFR-005 | Cross-platform .NET packaging including native parser payloads | §5.1 |
| NFR-006 | Least-privilege database access; introspection is read-only | §32 |
| NFR-007 | No secret or user data captured in semantic snapshots | §33 |
| NFR-008 | Analysis performance adequate for large schemas and corpora | §13 |
| NFR-009 | Inspectability: every decision traceable to inputs | P-020 |
| NFR-010 | Semantic snapshots are content-addressable / hashable | §7, §144.12 |
| NFR-011 | Native dependency update and security review path | Q-029 |
| NFR-012 | Explicit versioning of policy and model schemas | §11, §128 |

## D. Architecture decisions

The notebook's `D-001`–`D-020` (§142) are carried forward. Status after the
Phase 3 challenge and the two spikes:

| ID | Decision | Status after challenge |
|---|---|---|
| D-001 | Name: Strata | settled |
| D-002 | PostgreSQL first | settled |
| D-003 | Use a real PostgreSQL parser | settled, evidenced `EV-STRATA-2026-7A31` |
| D-004 | libpg_query-based .NET wrapper | **resolved to a specific package**, see `DF-STRATA-2026-4C7A` |
| D-005 | Native AST is not the durable public model | settled |
| D-006 | Strata builds a smaller canonical model | settled |
| D-007 | Direct SQL remains allowed | settled |
| D-008 | No mandatory meta-query language | settled |
| D-009 | Guardrails target effects, not complexity | settled |
| D-010 | Convergence only where safe and unambiguous | settled, deferred to P5 |
| D-011 | Explicit transitions remain necessary | settled, deferred to P7 |
| D-012 | Reference data is first-class desired state | settled, deferred to P6 |
| D-013 | Reference data has ownership semantics | settled, deferred to P6 |
| D-014 | Unexpected reference rows are not deleted by default | settled, deferred to P6 |
| D-015 | Renames never execute from similarity inference alone | settled |
| D-016 | Deployment execution follows analysis in the roadmap | settled |
| D-017 | High-impact decisions need provenance | settled |
| D-018 | Unknown/incomplete analysis stays explicit | settled, **strengthened** by `EV-STRATA-2026-B9C4` |
| D-019 | Targeted retrieval over dumps | settled |
| D-020 | Agent leverage must be demonstrated experimentally | settled, becomes `HY-STRATA-2026-1E5D` |

Decisions added by this analysis, not present in the notebook:

| ID | Decision |
|---|---|
| `DF-STRATA-2026-4C7A` | Adopt `pgsqlparser` 1.0.0 as the PostgreSQL parser adapter, with `PostgresQuery` as the documented arm64/musl fallback |
| `DF-STRATA-2026-9B2E` | Strata implements its own lexical scope resolution over the parse tree before emitting any dependency edge |
| `DF-STRATA-2026-D3F8` | Strata adopts SDE's four-tier architecture with the semantic tier referencing only `FSharp.Core`, mechanically checked |

## E. Hypotheses and experiments

These are the notebook's value claims. They are **not** product requirements;
promoting them would build an unfalsifiable roadmap.

| ID | Hypothesis | Notebook source | Experiment |
|---|---|---|---|
| `HY-STRATA-2026-1E5D` | Semantic retrieval reduces agent token consumption versus raw SQL corpus reading | §134, D-020 | Spike D |
| `HY-STRATA-2026-2A6F` | Semantic retrieval improves agent query correctness (fewer wrong joins, fewer hallucinated columns) | §14, §137 | Spike D |
| `HY-STRATA-2026-3C81` | Observed-join inference identifies real relationships the catalog does not declare, at usable precision | §9 | Spike C |
| `HY-STRATA-2026-4D92` | Offline catalog + AST resolution is sufficient for most references, without a live binder | §144.1, Q-005 | Spike B (remainder) |
| `HY-STRATA-2026-5E03` | `PREPARE`/`EXPLAIN` can substitute for implementing binder logic | Q-027 | Spike B (remainder) |
| `HY-STRATA-2026-6F14` | Deployment safety value is independent of agent-leverage value | §144.7 | deferred |

§134 is explicit that if agent correctness and cost do not improve materially,
agent context must not be the sole justification for Strata. `HY-STRATA-2026-6F14`
exists precisely so that a negative Spike D result does not invalidate the
program.

## F. Risks

From notebook §135 (deployment), §134 (core idea), §144, plus spike findings.

| ID | Risk | Severity | Source |
|---|---|---|---|
| RK-001 | Parse tree is not a bound tree; naive extraction yields **false** edges | high | `EV-STRATA-2026-B9C4` |
| RK-002 | `SELECT *` readers invisible to column-level impact analysis | high | `EV-STRATA-2026-B9C4` |
| RK-003 | Parser major version differs from live database major | medium | §5.1, Q-026 |
| RK-004 | Incomplete permissions make objects invisible; absence misread | high | §135.1, P-009 |
| RK-005 | Hidden external database consumers not indexed | high | §135.2, Q-013 |
| RK-006 | `search_path` ambiguity resolves to the wrong object | high | §135.11 |
| RK-007 | Dynamic SQL defeats dependency extraction | medium | §34, §135 |
| RK-008 | Extension-owned objects treated as Strata-managed | medium | §135.13 |
| RK-009 | Generated/system objects treated as drift | medium | §135.14 |
| RK-010 | Rename inferred incorrectly | high | §135.3, D-015 |
| RK-011 | Concurrent change invalidates the plan baseline | high | §135.4/5 |
| RK-012 | Lock impact underestimated | high | §135.6 |
| RK-013 | Nontransactional operation partially succeeds | high | §135.8 |
| RK-014 | Network failure leaves unknown execution state | high | §135.9, §144.13 |
| RK-015 | Reference data misclassified as Strata-owned | high | §135.10, §144.5 |
| RK-016 | False-positive guardrails cause social bypass | medium | §136 |
| RK-017 | Relationship inference creates false confidence | medium | §134, §144.6 |
| RK-018 | Canonical model becomes an endless taxonomy project | medium | §134, P-014 |

## G. Open questions

The notebook's `Q-001`–`Q-030` (§131) are preserved in full. Status:

**Resolved by this run's evidence:**

- `Q-001` (which parser wrapper) → resolved to `pgsqlparser` 1.0.0 by `EV-STRATA-2026-7A31`.

**Partially resolved:**

- `Q-005` (how much binding offline) → partially answered by `EV-STRATA-2026-B9C4`: qualified references resolve from the AST; unqualified relations need `search_path`; unqualified columns and `SELECT *` need catalog column lists; CTE scope must be resolved by Strata. The live-server comparison is outstanding.
- `Q-002` (minimum PostgreSQL majors) → constrained: the selected parser reports PostgreSQL 17.5. The supported-major policy is still undecided.

**Blocking for their phase, unresolved:**

`Q-003` is **ANSWERED** by `DF-STRATA-2026-B1E7` (desired state is per-object
declarative files, DACPAC-shaped) and `DF-STRATA-2026-A4D9` (a `strata.json`
manifest plus `schema/<schema>/<type>/<name>.sql`). `Q-004` remains open and is
now the binding constraint, because stable object identity is what rename
detection needs (§86); without it a rename is indistinguishable from a drop
plus an add, and `ER-010` forbids treating that as authorized.
`Q-010`, `Q-011`, `Q-012`, `Q-020` block persistence but not the diff itself. `Q-006`, `Q-027` block deep validation (P2).
`Q-007`, `Q-021`, `Q-024` block the policy profile work (P2).
`Q-008`, `Q-014`, `Q-015`, `Q-016`, `Q-019`, `Q-025`, `Q-030` block deployment
(P5+). `Q-009`, `Q-013`, `Q-017`, `Q-022`, `Q-023`, `Q-028`, `Q-029` are
non-blocking for the first slices.

**Not blocking any current work:** `Q-018` (which metrics prove agent leverage)
is answered operationally by the Spike D design rather than by a decision.

None of these were silently answered.

## H. Deferred / later possibilities

From §138, preserved so they are not rediscovered as new ideas: automatic
destructive deployment; universal dialect model; IDE UI; sophisticated
visualization; AI migration autonomy; automatic optimization; full query
lineage; runtime telemetry ingestion; role/security management; zero-downtime
orchestration; broad migration-framework replacement.

SQL Server support via `Microsoft.SqlServer.TransactSql.ScriptDom` (§143) is
deferred but the parser adapter boundary (PR-001) is designed to accept it.

## I. Explicit non-goals

From §140. These protect the architecture and are preserved as rules:

| ID | Strata must not become |
|---|---|
| NG-001 | Another SQL parser |
| NG-002 | Another SQL language |
| NG-003 | Another ORM |
| NG-004 | A "magic AI DBA" |
| NG-005 | A tool that guesses destructive intent |
| NG-006 | A schema diff that assumes absence means delete |
| NG-007 | A universal abstraction that erases dialect strengths |
| NG-008 | A migration engine built before the semantic model is trustworthy |
| NG-009 | A policy engine that confuses style with safety |
| NG-010 | An agent context mechanism that replaces giant SQL files with a giant JSON file |

## Phase 3 challenge: what this analysis removed or demoted

Applying the thirteen challenge questions changed the plan in these ways.

**Demoted from requirement to experiment.** Agent token reduction and agent
correctness were the notebook's most prominent motivation but are untested
value claims. They are now `HY-STRATA-2026-1E5D` and `-2A6F`. Building toward
them as requirements would have made the roadmap unfalsifiable.

**Demoted from first version to deferred.** Query plan / performance analysis
(§13) — `EXPLAIN` requires a live server and controlled timeouts, and
`EXPLAIN ANALYZE` executes the query. It adds live-execution risk before the
semantic model is trustworthy. Deployment planning and execution (§18, §28)
follow D-016 and §144.15.

**Rejected as accidental re-implementation.** Any Strata-side normalization of
every SQL construct into a universal AST (P-014, NG-002); any attempt to
re-derive PostgreSQL's binder rather than use catalog lookups plus, where
necessary, the server itself (P-019, Q-027).

**Kept despite roadmap cost.** Resolution-state modelling (PR-011) and
provenance (PR-014) are load-bearing safeguards, not polish: removing them
produces exactly the false-confidence failure the notebook's §144.2 warns
about. Analysis-scope bounding (PR-021) is likewise kept in the first version.

**Promoted from implicit to explicit.** Lexical scope resolution (PR-010) was
not a named notebook requirement. The CTE-shadowing case proves it is a
correctness prerequisite for every dependency claim, so it becomes a
first-slice requirement.

## Findings that change the notebook's assumptions

1. **Dependency extraction from a parse tree can be *wrong*, not merely
   incomplete.** `WITH orders AS (...) SELECT id FROM orders` yields a
   `RangeVar` named `orders` that a naive extractor attributes to the real
   `orders` table. The notebook treats the parse/bind gap as an analyzability
   limit (§144.1); it is also a false-edge hazard. `RK-001`.

2. **`SELECT *` silently defeats column-level impact analysis.** The
   notebook's flagship guardrail example — "drop column blocked because 8
   readers found" (§130) — is unsound unless `*` is expanded against a catalog
   snapshot or the statement's column analysis is explicitly marked incomplete.
   `RK-002`.

3. **The parser pins PostgreSQL 17.5.** Q-002 and Q-026 are immediate, not
   theoretical: a live database of another major can accept syntax this parser
   rejects, and vice versa.

4. **`PgQuery` on NuGet is not a parser.** The notebook lists two wrapper
   candidates; a third similarly named package is an Npgsql query builder and
   would be a trap for anyone searching by name.

5. **Package discovery is constrained in this environment.** NuGet's search
   index is blocked by egress policy, so wrapper selection was made over the
   candidates the notebook named plus exact-id probes. A better wrapper may
   exist that could not be discovered here.
