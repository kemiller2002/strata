---
id: DF-STRATA-2026-6C48
title: Documentation is executed before it is published, and reports evidence that contradicts it
status: accepted
decision_type: engineering-practice
created: 2026-09-13
updated: 2026-09-13
created_by_agent: claude
confidence: high
supersedes: []
superseded_by: []
evidence: [EV-STRATA-2026-F4C6, EV-STRATA-2026-D8E1, EV-STRATA-2026-D7B2, EV-STRATA-2026-E3D7]
tags: [strata, documentation, readme, er-008, evidence, agents]
---

# Decision Record

## Decision

Documentation in this repository is held to the same standard as the tool it
describes. Specifically:

1. **Every transcript is executed, not written.** A command and its output that
   appear in a document were run, and the output was pasted.
2. **Every figure is measured or cited.** A number is either produced in the act
   of writing or carries the identifier of the record it came from. No recalled
   numbers, no estimates presented as measurements.
3. **Evidence outranks the brief.** Where the records contradict the claim the
   documentation was asked to make, the documentation reports the records.
4. **Real output is not tidied.** Noise that a real run produces stays in.
5. **What is NOT covered is named as prominently as what is.** `ER-008` applied
   to prose.
6. **Agent-facing documentation says where the tool is the wrong choice.**
7. **Claims about this repository's own state are verified against it**, never
   recalled.

Where a rule can be checked mechanically, it is, and the check runs in CI.
The requirements themselves, with the failure that motivated each, are
`DOC-001`–`DOC-008` in
[`docs/strata/DOCUMENTATION-REQUIREMENTS.md`](../../docs/strata/DOCUMENTATION-REQUIREMENTS.md).

## Why

### The tool's central rule applies to its own documentation

`ER-008` says unknown, unsupported, unparsed, ambiguous, inaccessible and absent
are distinct states, never collapsed. Five defects were shipped and found where
Strata itself violated that — a difference it never compared rendered exactly
like convergence.

Documentation collapses states the same way and nobody notices, because there is
no test. A tidied transcript collapses *"this is what happens"* into *"this is
what I expect happens."* An unmeasured figure collapses *"measured"* into
*"plausible."* A coverage table that lists only what works collapses *"not
covered"* into *"fine."* A tool whose whole thesis is that those collapses are
the dangerous failure cannot ship documentation that commits them.

### It was tested in the writing, and the rules earned their place

Writing `README.md` and `docs/strata/AGENT-GUIDE.md` (WI-0102) produced four
concrete failures, each of which is now a requirement:

| What happened | Requirement |
|---|---|
| Three transcripts were written from expectation. All three were wrong when run. | `DOC-001` |
| A `--brief` saving was about to be quoted from a 2026-09-11 prediction of "roughly two thirds". Measured, it is 67% — but the *break-even* is the second query, which the prediction did not contain. | `DOC-002` |
| The brief asked for metrics showing Strata cuts token cost. The evidence says correctness improved in none of four regimes and a realistic session cost **8.7% more**. | `DOC-003` |
| A tidied quick-start showed `0 suppressed`. A real fresh database always reports **2**, and the suppressed list is the thing the README argues is the product. | `DOC-004` |

The most instructive was the fourth. The tidy version was not merely inaccurate,
it *undercut the document's own argument*: a README insisting that the
not-compared list is the point, illustrated with output showing an empty one.

### Being wrong about the repository is a distinct failure

While answering a question about coverage, this agent told the user a work item
had been captured. It had not — the capture was described in prose and the
command was never run. Nothing detected it until the queue was read two turns
later.

That is not a documentation-quality problem, it is a claim about state made from
memory. It gets its own requirement (`DOC-008`) because the fix is different:
not "write more carefully" but "read the record before asserting it exists."

## What this costs

Executing every transcript needs a working environment — a database, a build —
so documentation cannot be written from a cold checkout. That is a real cost and
it is accepted: the alternative is transcripts that are wrong three times out of
three, which is the measured rate.

Reporting contradicting evidence costs the persuasiveness of the document. A
README that says "the evidence does not currently support this" will convince
fewer people to adopt Strata. That is the correct outcome when it is what the
evidence says, and a reader who adopts on a claim the repository's own records
refute would have found out later and trusted everything else less.

## Alternatives rejected

**Review instead of rules.** Rejected: a reviewer cannot tell an executed
transcript from a plausible invented one by reading it. That is exactly why all
three invented ones survived until they were run.

**Enforce everything mechanically.** Rejected as impossible today. A script can
check that a flag exists and that a link resolves; it cannot check that a
transcript's *output* is real, that a figure was measured, or that a
contradiction was reported. Those stay human obligations, written down so they
can at least be pointed at. `DOC-001` through `DOC-004` are unenforced on
purpose, and the requirements document says so rather than implying coverage it
does not have.

**Put these in `AGENTS.md`.** Rejected: `AGENTS.md` is the ROS agent contract for
working on this repository generally. These are requirements about a deliverable,
with evidence and traceability, and they belong where the other requirements are.
