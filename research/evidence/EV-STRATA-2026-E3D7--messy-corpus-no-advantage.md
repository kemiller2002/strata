---
id: EV-STRATA-2026-E3D7
title: Even on a deliberately messy corpus agents match Strata exactly (6/6 both); the fixture did find a real Strata bug
research_area: strata-agent-leverage
evidence_type: test-result
source_title: Strata Spike D part 7 — messy corpus
source_author: claude
source_uri: 512-file messy fixture; two interactive agent runs; PostgreSQL 16.15
source_date: 2026-09-11
retrieved: 2026-09-11
created_by_agent: claude
confidence: high
supports: []
contradicts: [HY-STRATA-2026-2A6F]
related_theories: []
tags: [strata, agent-leverage, spike-d, messy-corpus, null-result, bug-found]
---

# Evidence Record

## Evidence summary

`EV-STRATA-2026-D7B2` concluded that `grep` beat Strata on a uniformly
schema-qualified corpus, and named the untested regime where a resolved graph
*should* win: unqualified names, shadowing, cross-schema collisions, dynamic
SQL. This built that fixture.

**It did not discriminate.** Both agents scored **6/6 recall, 6/6 precision**,
identical to Strata. Every trap was correctly rejected by both.

The fixture was not wasted: it **found a real Strata bug** that had been
silently suppressing bare-name column dependencies.

## Exact claim supported or contradicted

**Contradicts `HY-STRATA-2026-2A6F` in the last regime that plausibly favoured
it.** The correctness hypothesis has now been tested in four regimes — small
clean, small seam, large clean, small messy — and has held in none.

## Method

512 files, five schemas. The question: *which files read
`billing.orders.status`?* Ground truth is six files, and the fixture is built
so that every naive strategy fails differently:

| Trap | Defeats |
|---|---|
| `orders` exists in `billing`, `archive` AND `staging` | bare `grep orders` |
| 3 files use a bare `orders` resolved by `search_path` | `grep billing.orders` |
| 1 file references the column only through an alias | `grep orders.status` |
| 1 file is `SELECT *` | `grep status` |
| 1 file is a CTE named `orders` | any text match |
| 1 file mentions the column only in a `--` comment | any text match |
| 1 file builds the target with `quote_ident()` at runtime | everything |

### What raw text search alone gives

```
grep 'billing.orders.status'   -> 1 file, and it is the COMMENT. 0/6 correct.
grep 'billing.orders'          -> 4 files: 3 correct, 1 false positive, misses all 3 bare.
grep 'orders' AND 'status'     -> 11 files: 5 correct, 6 false positives, misses SELECT *.
                                  recall 5/6, precision 45%.
```

### What the agents gave

Both: **6/6 recall, 6/6 precision**, 6 tool calls, ~40 s. Both correctly
excluded all six traps with accurate reasons. One volunteered the `SELECT *`
judgement call unprompted:

> *"orders-dump.sql reads status only implicitly, via SELECT *. If your
> definition of 'reads' requires the column to be named explicitly, drop it …
> I included it because the star does genuinely pull the column into the
> result set."*

### What Strata gave

**6/6 recall, 6/6 precision** — but only after the bug below was fixed. Before
the fix: 3/6, missing every bare-name file.

## The bug this fixture found

`CatalogResolution.columnDependencies` resolved a statement's relations using
`ScopeResolution.dependencyEdgeCandidates`, which is **catalog-blind**. From the
statement alone it cannot know which schema on a multi-entry `search_path`
actually holds a bare table name, so it must return `Ambiguous` — and an
ambiguous relation contributes no column dependencies at all.

The catalog-aware resolver (`resolveRelationName`) existed in the same module
and was simply not used by this path. With `search_path = billing, public` and
`orders` present only in `billing`, the catalog resolves it unambiguously.

Fixed so scope resolution still runs **first** — deciding what is a database
object at all, which keeps the CTE exclusion and `RK-001` intact — and the
catalog is consulted only afterwards. Three regression tests cover it,
including a control proving a genuinely ambiguous name (two schemas both
holding `orders`) still yields nothing rather than a guess.

**Implication for earlier records:** every prior corpus was uniformly
schema-qualified, so this bug never fired. It did not affect
`EV-STRATA-2026-D7B2`'s conclusion, but it means Strata's measured capability
was understated on any corpus with bare names.

## Interpretation

**The agent-leverage thesis is not supported in any regime tested.** Four
fixtures, thirteen agent runs. Agents with search tools and reasoning have
matched or beaten Strata every time, including on unqualified names, CTE
shadowing, cross-schema collisions, comment noise and `SELECT *` — the exact
cases the semantic model was built to handle.

The reason is now clear and worth stating plainly: **the hard part of these
questions is not retrieval, it is reasoning, and the agent already has that.**
Strata resolves `SELECT *` by expanding against a catalog; the agent resolves it
by knowing what `SELECT *` means. Strata excludes a CTE by scope resolution; the
agent excludes it by reading three lines. Both arrive at the same place.

**What Strata still has, measured here:**

| | Strata | Agent |
|---|---|---|
| Correctness | 6/6 | 6/6 |
| Latency | **1,983 ms** | 40,545 ms |
| Determinism | identical every run | two runs agreed; n=2 |
| Output | machine-readable, typed, evidenced | prose |

The 20x latency advantage is real but is the inverse of the large-corpus result
(where Strata was ~9,000x slower), so it is a property of corpus size, not of
the approach.

**The determinism point is the one that has not been falsified.** Strata gives
the same answer every time and can be diffed, asserted on in CI, and audited. An
agent gave the right answer twice. Those are different kinds of guarantee, and
nothing measured here distinguishes their value — but a CI gate cannot be built
on a probabilistic answer, and that is a use case agents do not serve.

Per notebook §134, applied for the fourth time: agent context must not be
Strata's justification.

## Limitations

1. **Two agent runs, one model.** A weaker or faster model might not reason
   through `SELECT *` and CTE shadowing so cleanly. This tests a capable model.
2. **The agents were TOLD the `search_path`** in the prompt, as documented
   context an engineer would have. Without it they would likely have missed the
   bare-name files. Strata read it from the connection.
3. **512 files, one question.** Not a scale test; `EV-STRATA-2026-D7B2` covers
   that and reaches the opposite conclusion on latency.
4. The fixture is my own construction, and I chose the traps.

## Counterevidence

The latency and determinism columns are genuine counterevidence to a flat "just
use an agent" conclusion. So is the bug: a deterministic tool's defect is
findable and fixable once, whereas an agent's reasoning error is per-invocation
and invisible.

## Reproduction or verification notes

Build the 512-file fixture with the seven traps above, set
`search_path=billing,public` on the connection, and compare
`strata impact billing.orders.status` against an agent given shell access and
the same question. Ground truth is the six files listed in
`docs/strata/experiments/`. Verified 2026-09-11.
