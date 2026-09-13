# Strata for agents

How an LLM agent should drive Strata, and how to keep it cheap.

This is written to be read by an agent. It is prescriptive on purpose: where
there is a right answer it says so, and where the evidence says *don't bother*
it says that too.

---

## 1. First, the part most guides would leave out

**Strata is often the wrong tool for a search question.** This is measured, not
modesty.

On a 2.37M-token corpus, `grep` answered the discriminating cross-module question
in **15 ms**; Strata took **247,000 ms** — about **16,000x slower** — and agents
with plain shell tools answered *all four* seam questions correctly without it
([`EV-STRATA-2026-D7B2`](../../research/evidence/EV-STRATA-2026-D7B2--advantage-does-not-survive-scale.md)).
On a deliberately messy corpus — unqualified names, shadowing, cross-schema
collisions, dynamic SQL — both conditions scored 6/6 recall and precision
([`EV-STRATA-2026-E3D7`](../../research/evidence/EV-STRATA-2026-E3D7--messy-corpus-no-advantage.md)).

So:

### Decision table

| Your question | Use |
|---|---|
| "Which files mention `legacy_code`?" | **`grep`** |
| "What columns does `app.orders` have?" | **`grep` the .sql file**, or `strata inspect` if you have no source tree |
| "Is this SQL I just wrote actually valid?" | **`strata validate`** ← no good alternative |
| "Will this migration break something?" | **`strata check`** ← no good alternative |
| "Does production still match what we shipped?" | **`strata drift`** ← no good alternative |
| "Deploy this schema" | **`strata deploy`** ← no good alternative |
| "What reads this column, including `SELECT *`?" | `strata impact` — but verify with `grep` |
| "How do these two tables relate?" | `strata path`, on a large schema you cannot read |

**The pattern:** Strata's unique value is in the *write* path — validating,
gating, compiling, deploying, detecting drift. Those are things `grep` structurally
cannot do. For *reading* a schema you already have on disk, ordinary tools are
usually faster and just as correct.

Reach for `strata inspect` when the schema is large, you do **not** have the
source tree, and you need one specific answer.

---

## 2. The session pattern that keeps it cheap

Every Strata answer carries a **scope block** — what was and was not analysed.
That block is the honesty design, and for a point query it is *larger than the
answer it qualifies*. It is also **identical for every query against one
snapshot**, so repeating it is pure waste.

Fetch it once. Use `--brief` thereafter.

```bash
# Once, at the start of a session:
strata scope --json

# Every query after that:
strata inspect app.customer --json --brief
strata relationships app.orders --json --brief
```

### Measured, on the `sales` fixture

| Payload | Bytes |
|---|---|
| `inspect --json` (full scope inline) | 2,715 |
| `inspect --json --brief` | **883** |
| `scope --json` (once per session) | 2,049 |

A 67% cut per answer. Break-even is the **second query**:

| Queries | Full | `scope` + `--brief` | Saving |
|---|---|---|---|
| 1 | 2,715 | 2,932 | −8% (worse) |
| 2 | 5,430 | 3,815 | 30% |
| 5 | 13,575 | 6,464 | 52% |
| 10 | 27,150 | 10,879 | 60% |
| ∞ | — | — | 67% |

**One-shot query? Skip `--brief`.** It costs you 8%. From the second query on, it
pays.

### `--brief` never hides anything that changes the answer

It replaces the full scope with a `scopeDigest` and a `fullScopeCommand`, but
keeps inline:

- `caveats` — anything that changes how the result must be read
- `supportsAbsenceClaim` — whether "no dependency found" is *evidence* of none
- `caveatsElided` — a count, so you know if there are more

```json
{
  "query": "inspect",
  "target": "sales.orders",
  "result": { "...": "..." },
  "scopeDigest": "...",
  "supportsAbsenceClaim": false,
  "caveats": [],
  "caveatsElided": 0,
  "fullScopeCommand": "strata scope"
}
```

**If `supportsAbsenceClaim` is `false`, an empty result is not evidence of
absence.** Do not report "nothing depends on this" — report "no dependency found,
and the analysed scope does not support concluding none exists."

---

## 3. Exit codes — check them, do not parse prose

| Code | `plan` / `apply` / `deploy` / `check` | `validate` | `drift` | `compile` |
|---|---|---|---|---|
| **0** | allow, **or already matches** | valid | matches | compiled |
| **1** | **block** | provably wrong | differs | project is wrong |
| **2** | requires approval | unverifiable | could not tell | unreadable |

Three traps:

1. **`plan` exits 0 when the database already matches**, whatever the gate would
   have said. An empty plan from a diff is convergence, not a problem. Do not
   read exit 0 as "the gate approved" — read the change count.
2. **`validate` exit 2 is not a pass.** `unverifiable` means Strata could not
   determine whether the SQL is correct. Treat it as "unknown", never "fine".
3. **Exit 1 from `check` is a block and is never overridable.** `--approve` does
   not touch it. If you are tempted to work around a block, stop and report it.

---

## 4. Reading a plan correctly

A plan has **two** lists, and the second is the one agents skip.

```
PLAN: 0 change(s), 3 suppressed  —  gate verdict REQUIRES-APPROVAL

  (no changes proposed)

NOT PROPOSED — differences Strata saw and will not act on:

  app.summary                  [not-compared]
      materialized view exists on both sides; its definition was NOT compared,
      so the bodies may differ
```

**`0 change(s)` plus a `not-compared` line does not mean converged.** It means
"nothing differs among the things I compared, and here is what I did not
compare." Always report the suppressed list.

The suppression reasons and what each means for you:

| Reason | What to do |
|---|---|
| `not-compared` | Strata looked and could not compare. **Report it.** Never call this converged. |
| `not-modelled` | Exists; Strata does not model this kind. Usually fine — but say so. |
| `outside-managed-schemas` | Not your project's. Leave alone. |
| `extension-owned` | An extension owns it. Leave alone. |
| `desired-state-incomplete` | A file failed to load. **Fix that first** — every removal is suppressed while it holds. |
| `drops-not-enabled` | Would be dropped; needs `--allow-drops`. A deliberate human decision. |

---

## 5. Writing SQL an agent can trust

The single highest-value loop:

```bash
# Write the SQL, then before proposing it:
strata validate proposed.sql --types
```

`--types` has the server `PREPARE` each statement inside a rolled-back
transaction. It catches what a name check cannot: `WHERE total = 'abc'` on a
numeric column, a function signature that does not exist, an ambiguous column in
a join.

Then, for anything that changes the schema:

```bash
strata check proposed.sql
```

### Validate with no database at all

In an editor, a pre-commit hook, or a PR from a fork:

```bash
strata validate proposed.sql --artifact app.strata --search-path app,public
```

No connection and no credentials. It also asks the better question: SQL valid
against the artifact but failing against production has found a **stale server**,
not a bad query.

### What validation does and does not reach

| Body | Checked? |
|---|---|
| Plain SQL statements | ✅ names and columns resolved |
| `LANGUAGE sql` routine bodies | ✅ recursed into and resolved |
| `LANGUAGE plpgsql` bodies | ⚠️ **syntax only** — references reported `unverifiable` |
| Other languages (C, …) | ⚠️ `unverifiable` |
| Dynamic SQL | ⚠️ `unverifiable` |

---

## 6. Deploying without lying about it

```bash
strata compile --project . --out app.strata     # needs any PG of the right major
strata sign --artifact app.strata --key strata.key
strata deploy --artifact app.strata --confirm --public-key strata.pub --require-signature
```

Rules an agent must not break:

- **Never** pass `--confirm` on a plan you have not shown the human.
- **Never** pass `--approve` to get past something you do not understand. It
  restates each finding as approved in the output — that is a record with your
  name on it.
- **Never** pass `--allow-drops` unless removal is the explicit ask.
- `compile` **refuses** on any file it could not read or parse. Do not work
  around that by removing the file from `include` — an artifact is a claim that
  the project was read whole.
- The same artifact bytes go to staging and production. Do not recompile between
  environments; that discards the guarantee.

---

## 7. Copy-paste system prompt

```text
You have `strata`, a declarative PostgreSQL schema tool.

USE IT FOR (no good alternative exists):
  strata validate <f.sql> [--types]   verify SQL before proposing it
  strata check <f.sql>                gate a schema change
  strata plan | apply --confirm       converge a project to a database
  strata compile --out <f> / deploy --artifact <f>
  strata drift --artifact <f>         has the target drifted?

DO NOT use it as a search engine. For "which files mention X", use grep —
it is measured at ~16,000x faster on large corpora and just as correct.

SESSION PATTERN (cheaper from the 2nd query onward):
  strata scope --json                 once
  strata <query> --json --brief       thereafter

READING RESULTS:
  - Exit 2 from validate is UNVERIFIABLE. It is NOT a pass.
  - Exit 1 from check is a BLOCK. It is never overridable. Report it; do not
    route around it.
  - plan exits 0 when the DB already matches. Read the change count, not just
    the code.
  - A plan's "NOT PROPOSED / not-compared" list means Strata did not compare
    those. "0 changes" plus a not-compared line is NOT convergence. Always
    report that list.
  - If supportsAbsenceClaim is false, an empty result is not evidence of
    absence. Say "no dependency found, scope does not support concluding none
    exists."

NEVER: pass --confirm on a plan the human has not seen; pass --approve to get
past something you do not understand; pass --allow-drops unless removal was
explicitly requested.
```

---

## 8. Things that will bite you

| Symptom | Cause |
|---|---|
| Everything reports `not-compared` | The connected role cannot `CREATE SCHEMA`, so shadow normalisation cannot run. Grant it, or accept degraded comparison. |
| `deploy` refuses outright | Target PostgreSQL **major** differs from the compiling server. Expressions in an artifact are the compiling server's rendering. |
| A `.sql` file is ignored | It is not in `strata.json` → that is an **error**, not a skip. Run `strata sync`. |
| An artifact is refused by name | Format version skew. Recompile with the same build that deploys. |
| A materialized view never converges | Matviews are **presence-only**; their bodies are not compared. Disclosed, not silent. |
| An enum will not converge | PostgreSQL has no `ALTER TYPE ... DROP VALUE` and cannot reorder. Recreate the type. |
| A domain will not converge | Base type or collation differs, and neither can be altered. Everything else on that domain stops being compared too. |

---

## 9. Before you claim a saving

The project's own evidence says the agent-context thesis is **largely
unsupported**:

- Correctness tested in four regimes — small clean, small seam, large clean,
  small messy — and improved in **none**.
- A realistic 5-question session at 4 tables used **8.7% more** tokens with
  Strata than without.
- The 46x–101x context reduction is real but measured against an agent reading
  the *whole* schema, on a synthetic uniform 200-table fixture, with answers later
  found to be missing constraints and indexes.

Measure on your own schema before asserting a saving. The `--brief` numbers in
§2 are the one figure here you can rely on, because they are a property of the
output format rather than of anyone's agent.
