# Strata known risks

Derived from `docs/strata/REQUIREMENTS-ANALYSIS.md` section F, which carries the
full list with notebook sources. Risks marked **evidenced** were demonstrated by
a spike in this repository, not inferred from the design notebook.

## Analysis-correctness risks

| ID | Risk | Likelihood | Impact | Mitigation |
|---|---|---:|---:|---|
| RK-001 | **Evidenced.** Parse tree is not a bound tree; naive extraction emits *false* dependency edges (CTE name shadowing a table name) | High | High | `DF-STRATA-2026-9B2E`: Strata resolves lexical scope before emitting any edge; invariant test asserts an unresolved reference never becomes a declared dependency |
| RK-002 | **Evidenced.** `SELECT *` readers are invisible to column-level impact analysis, so "N readers found" undercounts | High | High | Expand `*` against a catalog snapshot, or mark the statement's column analysis explicitly incomplete; never report zero column references silently |
| RK-003 | Parser major (PostgreSQL 17.5) differs from the live database major | Medium | Medium | Report both versions in every output (`NFR-004`); `Q-002`/`Q-026` open |
| RK-006 | `search_path` ambiguity resolves to the wrong object | Medium | High | Resolution state `ambiguous` is first-class; never guess |
| RK-007 | Dynamic SQL defeats dependency extraction | High | Medium | Degrade analyzability explicitly; dynamic SQL is a recorded analysis gap, not an absence of dependencies |
| RK-017 | Relationship inference creates false confidence | Medium | High | Observed relationships never become declared FKs; categorical certainty with evidence counts, no artificial decimal scores |
| RK-018 | Canonical model becomes an endless taxonomy project | Medium | Medium | `ER-014`: add semantic concepts only when a current requirement needs them |

## Visibility and scope risks

| ID | Risk | Likelihood | Impact | Mitigation |
|---|---|---:|---:|---|
| RK-004 | Incomplete permissions make objects invisible; absence misread as deletion intent | High | High | `P-009` fail closed; introspection completeness reported per category; `NG-006` |
| RK-005 | Hidden external database consumers not indexed | High | High | `PR-021`: every impact claim bounded by declared analysis scope; `Q-013` open |
| RK-008 | Extension-owned objects treated as Strata-managed | Medium | High | Explicit managed/observed classification before any deletion |
| RK-009 | Generated/system objects treated as drift | Medium | Medium | Managed-object classification |

## Deployment risks (deferred phases, recorded now)

| ID | Risk | Impact | Mitigation |
|---|---|---:|---|
| RK-010 | Rename inferred incorrectly | High | `D-015`: never execute a rename from similarity inference |
| RK-011 | Concurrent change invalidates the plan baseline | High | Plan hashes; revalidation close to execution |
| RK-012 | Lock impact underestimated | High | Lock and statement timeouts; evidence-based warnings |
| RK-013 | Nontransactional operation partially succeeds | High | Explicit partial-state modelling |
| RK-014 | Network failure leaves unknown execution state | High | Unknown is a first-class outcome requiring re-introspection (`§144.13`) |
| RK-015 | Reference data misclassified as Strata-owned | High | `D-014`: unexpected rows never deleted by default; explicit ownership |
| RK-016 | False-positive guardrails cause social bypass | Medium | Consequence-based policy; legitimate expert escape hatch (`§144.10`) |

## Method risks

| Risk | Likelihood | Impact | Mitigation |
|---|---:|---:|---|
| Strata is the first non-HelixNote trial of SDE v0.2, whose outcome claims are EXPERIMENTAL | High | Medium | Record method cost and friction as engineering metrics; treat SDE overhead as measurable, not assumed |
| Agent-leverage hypotheses fail, removing the notebook's headline motivation | Medium | High | `HY-STRATA-2026-6F14` states deployment value independently; `§134` forbids using agent context as sole justification |
| Baseline selected after results are known | Low | Medium | Baseline recorded and tagged before implementation (`docs/baselines/BASELINE-20260911.md`) |
