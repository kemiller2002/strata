# Documentation requirements

Requirements for documentation in this repository — `README.md`, the agent
guide, and any future document that shows a reader how to use Strata.

**Authority:** [`DF-STRATA-2026-6C48`](../../research/decisions/DF-STRATA-2026-6C48--documentation-is-executed-before-it-is-published.md).
This document is the requirement list; the decision record is why they exist.

**Why these exist at all.** Strata's central rule (`ER-008`) is that unknown,
unsupported, unparsed, ambiguous, inaccessible and absent are distinct states,
never collapsed. Five shipped defects were exactly that collapse. Documentation
commits the same collapse and nothing catches it, because there is no test: a
tidied transcript turns *"what happens"* into *"what I expect"*, an unmeasured
figure turns *"measured"* into *"plausible"*, and a coverage table listing only
successes turns *"not covered"* into *"fine"*.

Every requirement below was written **after** the failure that motivated it, all
of which occurred while producing `README.md` and `docs/strata/AGENT-GUIDE.md`
under `WI-0102`.

## Summary

| ID | Requirement | Enforcement |
|---|---|---|
| `DOC-001` | Every transcript is executed, never written from expectation | human |
| `DOC-002` | Every figure is measured in the writing, or cites its record | human |
| `DOC-003` | Evidence that contradicts the brief is reported, not omitted | human |
| `DOC-004` | Real output is not tidied | human |
| `DOC-005` | Every command and flag shown must exist | **CI** |
| `DOC-006` | Every internal link must resolve | **CI** |
| `DOC-007` | What is not covered is named as prominently as what is | human |
| `DOC-008` | A claim about this repository's state is read, not recalled | human |

Six of the eight are human obligations. That is stated rather than hidden: a
requirements table that implied mechanical coverage it does not have would be
committing `DOC-004` against itself.

---

## DOC-001 — Every transcript is executed, never written from expectation

A command and its output shown in a document were **run**, and the output was
pasted. Reconstructing plausible output from knowledge of the tool is
prohibited, however well you know the tool.

**Motivating failure.** Three transcripts in the first draft of `README.md` were
written from expectation. All three were wrong:

| Written | Actual |
|---|---|
| `PLAN: 2 change(s), 0 suppressed` | `2 change(s), 2 suppressed` — `PUBLIC` on `public` and `plpgsql` are present on every fresh database |
| `PLAN: 0 change(s), 0 suppressed` | `0 change(s), 2 suppressed — gate verdict REQUIRES-APPROVAL` |
| A two-statement `validate` transcript | The file used had one statement |

Three for three. The author had built the tool in the same session.

**Why it is not merely tidiness.** The second case is the load-bearing one: a
converged plan reports `REQUIRES-APPROVAL` *while exiting 0*, which is precisely
the trap `docs/strata/AGENT-GUIDE.md` §3 warns agents about. The invented
transcript hid the behaviour the documentation exists to teach.

**Enforcement:** human. A script cannot distinguish real output from plausible
invented output — which is exactly how all three survived review until they were
run.

---

## DOC-002 — Every figure is measured in the writing, or cites its record

A number in documentation is either produced while writing the document, or
carries the identifier of the evidence record it came from. Recalled numbers and
estimates presented as measurements are prohibited.

**Motivating failure.** The `--brief` saving was about to be quoted from a
prediction in `EV-STRATA-2026-F4C6` (2026-09-11) that emitting scope once would
cut a session's cost "by roughly two thirds". Measured on the day of writing:

| Payload | Bytes |
|---|---|
| `inspect --json` | 2,715 |
| `inspect --json --brief` | 883 |
| `scope --json` | 2,049 |

The prediction was directionally right — 67% at the asymptote — and **missing the
number that matters**: the break-even is the *second* query, and a one-shot query
is 8% *worse* with `--brief`. An agent following the recalled figure would have
been told to always use it.

**Corollary.** Where a measured figure and a predicted one both exist, the
document reports the measurement and may note the prediction. It never reports
the prediction alone.

**Enforcement:** human.

---

## DOC-003 — Evidence that contradicts the brief is reported, not omitted

Where this repository's records contradict the claim a document was asked to
make, the document reports the records. This holds when the brief comes from a
user, a maintainer, or a roadmap.

**Motivating failure.** `WI-0102` was requested with: *"put metrics in the
readmes on how it cuts cost on tokens etc."* The records say:

| Record | Finding |
|---|---|
| `EV-STRATA-2026-D8E1` | Realistic five-question session: Strata used **8.7% MORE** tokens (48,870 vs 44,951). Correctness 15/15 in both conditions. |
| `EV-STRATA-2026-D7B2` | At 2.37M tokens with search tools, `grep` answered the discriminating question in 15 ms; Strata took 247,000 ms — **~16,000x slower**. Raw agents answered all four seam questions correctly. |
| `EV-STRATA-2026-E3D7` | Messy corpus — the last regime that plausibly favoured Strata: both conditions 6/6 recall, 6/6 precision. |
| `EV-STRATA-2026-F4C6` | 46x–101x context reduction at 200 tables — but versus an agent reading the *whole* schema, on a synthetic uniform fixture, and the answers measured were later found lossy. |

`HY-STRATA-2026-1E5D` records the honest position: supported for large schemas
with targeted single queries, **actively false** for small schemas with
multi-query sessions, crossover never located.

The README therefore contains a section titled *"What is measured, and what is
not"* which tells a prospective adopter the evidence does not currently support
the token-saving claim.

**Why.** A document making a claim the repository's own records refute is
discoverable by anyone who opens `research/evidence/`, and it costs the
credibility of everything else in the document. The cost of compliance is real —
fewer readers adopt — and is accepted.

**Enforcement:** human.

---

## DOC-004 — Real output is not tidied

Noise a real run produces stays in. Removing it to make an example read cleanly
is prohibited.

**Motivating failure.** The quick-start plan was tidied to `0 suppressed`. Every
fresh PostgreSQL database reports two suppressions — `PUBLIC` holds `USAGE` on
`public`, and `plpgsql` is installed by default — and Strata reports both rather
than quietly revoking or dropping either.

That tidy-up **contradicted the document's own argument**. `README.md` opens by
arguing that the suppressed list is the product, then illustrated it with an
empty one. The corrected version keeps both lines and explains that they appear
on every fresh database.

**Enforcement:** human, and partially structural — `DOC-001` compliance produces
untidied output automatically, because the output is pasted.

---

## DOC-005 — Every command and flag shown must exist

Every `strata <command>` and every `--flag` appearing inside a runnable code
fence must exist in the CLI's own `--help`.

**Why.** A copy-pasteable example that no longer works is worse than no example:
a reader pastes it, gets an error, and debugs it against the documentation that
caused it.

**Enforcement:** **`scripts/check-docs-match-cli.sh`**, run in CI.

It inspects only shell code fences — prose may legitimately name a flag, and
evidence-record filenames such as
`EV-STRATA-2026-F4C6--agent-context-cost.md` contain `--` sequences that are not
flags. Verified by inserting a bad flag and confirming a non-zero exit.

**A defect in the check itself, worth recording.** Its first version extracted
fences with a regex that recognised only ` ```bash `. A ` ```mermaid ` block's
*closing* fence was therefore paired with the next *opening* fence, swallowing
the prose between them, and five evidence-record filenames were reported as
undefined flags. It is now a line-by-line walk. A checker that produces false
positives gets disabled, so it commits the same failure as the docs it guards.

---

## DOC-006 — Every internal link must resolve

Every relative Markdown link in a document must point at a file that exists.

**Enforcement:** **`scripts/check-docs-match-cli.sh`**, run in CI.

---

## DOC-007 — What is not covered is named as prominently as what is

A coverage table, capability list or feature summary states its gaps in the same
table, in the same visual weight, as its successes. `ER-008` applied to prose.

**Applied.** `README.md`'s coverage table gives materialized views a row reading
*"Presence only — disclosed as not-compared"* beside the objects that do compare,
rather than omitting the row. Composite and range types appear as *"Refused by
name, loudly"*. The agent guide's validation table marks plpgsql bodies
*"syntax only"*.

**Why.** A reader scanning a feature list reads absence as "not applicable", not
as "does not work". `WI-0101` — a materialized view's definition is never
compared — would be invisible to anyone reading a table that listed only what
converges.

**Enforcement:** human.

---

## DOC-008 — A claim about this repository's state is read, not recalled

Before asserting that a record, work item, file or commit exists, read it. This
applies to conversational claims as much as to committed prose.

**Motivating failure.** While answering a coverage question, this agent told the
user: *"I've captured it as WI-0101."* No capture command had been run — the
action was described in prose and never performed. The queue still ended at
`WI-0100`, and nothing detected it until the queue was read two turns later for
an unrelated reason.

**Why it is separate from `DOC-001`.** The fix is different. `DOC-001` is "run
the command before showing its output"; this is "read the store before claiming
its contents". A statement about repository state is a factual claim with a
cheap, authoritative check — `.ros/work/queue.json`, `registries/*.json`, `git
log` — and the cost of being wrong is that a maintainer believes work is tracked
when it is not.

**Enforcement:** human. `scripts/ros-validate.sh` catches an *unattributed*
change, but nothing catches a capture that was never attempted.

---

## Traceability

| Requirement | Motivating failure | Work item | Enforcement |
|---|---|---|---|
| `DOC-001` | 3/3 invented transcripts wrong | WI-0102 | — |
| `DOC-002` | `--brief` prediction missing the break-even | WI-0102 | — |
| `DOC-003` | Brief asked for a claim the evidence refutes | WI-0102 | — |
| `DOC-004` | Tidied `0 suppressed` contradicted the README's thesis | WI-0102 | — |
| `DOC-005` | — (preventive) | WI-0102 | `scripts/check-docs-match-cli.sh` |
| `DOC-006` | — (preventive) | WI-0103 | `scripts/check-docs-match-cli.sh` |
| `DOC-007` | Matview gap (`WI-0101`) invisible in a successes-only table | WI-0102 | — |
| `DOC-008` | `WI-0101` reported as captured when it was not | WI-0103 | — |

Records: [`DF-STRATA-2026-6C48`](../../research/decisions/DF-STRATA-2026-6C48--documentation-is-executed-before-it-is-published.md)
· `EV-STRATA-2026-F4C6` · `EV-STRATA-2026-D8E1` · `EV-STRATA-2026-D7B2`
· `EV-STRATA-2026-E3D7` · `HY-STRATA-2026-1E5D`
