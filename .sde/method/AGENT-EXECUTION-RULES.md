---
id: SDE-METHOD-004
title: Agent Execution Rules
status: draft
version: 0.2.0
created: 2026-09-02
updated: 2026-09-06
related_documents:
  - method/CONSTRUCTION-METHOD.md
  - method/NAVIGATION-AND-CONTEXT.md
  - method/FEATURE-MANIFESTS.md
tags: [method, agent-rules]
---

# Agent Execution Rules

Status: EXPERIMENTAL as a claim of improved outcomes. The steps below are the
required v0.2 execution contract for an autonomous coding agent.

## The central finding this document operationalizes

In Experiment 3 [EV-HN-2026-0005], the hardened architecture's own
structural mechanical coverage for the mission's required sites was higher
than baseline's (52.6% vs. 42.1%, architecture-potential). But the
*executing agent* did not experience that improvement, because it invested
upfront reading before editing and found several genuinely-guarded sites by
reading rather than by triggering the compiler. Its as-experienced MDR was
*lower* than the baseline agent's (31.6% vs. 42.1%), even while its search
operations (-63%) and repair loops (-50%) were both substantially lower.

> Mechanical hardening's payoff for AI agent workflows specifically may
> depend on the agent's own operating strategy in a way it does not for
> human developers, who typically cannot "read the whole diff space" as
> fast as an LLM-based agent can.

## Rules

### Task discovery

1. Classify the requested change and identify the responsible semantic
   feature.
2. Read the repository semantic map if ownership is unclear.
3. Read the feature manifest—or the semantic map's explicit not-needed
   rationale for a small/obvious area—and the declared state, transitions,
   invariants, capabilities, interfaces/effect contracts, tests, and required
   composition context.
4. Record the expected modification boundary.

### Context check

5. Determine whether declared local context is sufficient to identify the
   authoritative representation and verification obligations.
6. If not, name the missing information and follow declared dependencies.
7. If still insufficient, perform targeted discovery; use repository-wide
   search only when the named gap cannot otherwise be resolved.
8. Record undeclared dependencies, unrelated reads, and unexpected context
   expansion as architecture/navigation findings.

### Execution

9. Make the smallest valid semantic change. Prefer authoritative transitions,
   guards, and capabilities over direct mutation.
10. Preserve one semantic authority across responsibility clusters and
    physical files.
11. Avoid cross-boundary edits unless justified under
    `method/NAVIGATION-AND-CONTEXT.md`; record the insufficient contract,
    boundary, manifest impact, and redesign question.

### Verification

12. Compile/typecheck and repair compiler-directed obligations before broad
    exploratory search.
13. Run architecture checks, boundary/contract checks, and focused behavioral
    tests in that order where applicable.
14. Perform targeted search for known unguarded representations, then
    integration and representative live verification when warranted.
15. Record available discovery/context metrics, defect classes, and evidence
    through the project's existing telemetry authority where one exists;
    discover capability state at T0 and label unavailable telemetry rather
    than estimating it.
16. Stop when acceptance and required verification are complete.

**Edit incrementally, then compile—after sufficient local discovery.** Do not
read the whole diff space first for a Semantic Change, but do not edit an
authority you have not located and understood. After the smallest
authoritative change, let compiler errors name the next mechanically guarded
site rather than pre-deriving the full propagation list by broad reading. The
causal benefit is unproven; see `doctrine/EVIDENCE-TO-ENGINEERING-MAP.md`.

**Distinguish exploratory search from targeted search.** Exploratory search
("what does this codebase look like") is what dropped 63% between
conditions in Experiment 3. Targeted search (checking a specific tier the
mechanical checks are known not to cover, e.g. the API/HTTP tier) remains
necessary regardless of architecture and should not be skipped because
mechanical checks passed.

**Treat an undeclared dependency as architectural evidence.** Do not silently
normalize it as implementation inconvenience. Determine whether the manifest
is incomplete, the public contract is insufficient, internals are leaking,
shared behavior is misplaced, or the semantic boundary is wrong.

**Use nested agent instructions only as local routers.** Repository
instructions establish global execution policy; feature-local instructions may
state local commands and boundaries, but must point to the feature manifest
and authority rather than duplicate semantic rules.

**Never treat a green compiler/architecture/contract-check run as proof of
behavioral correctness for a fan-out or dispatch arm.** Run the behavioral/
integration check from `method/VERIFICATION-METHOD.md` explicitly, every
time, for this defect class — it has a 100% miss rate across every
mechanical check tried in two independent experiments.

**Record self-classification honestly, including when it is unflattering.**
Experiment 3's two conditions used different self-classification strictness
for what counted as "found by the compiler" vs. "found by reading" — record
which one you used and why, rather than silently picking whichever
attribution looks better.

**Never report agent-self-reported token/cost/duration figures as
authoritative without labeling their source.** Experiment 3 found the
harness's own tool-spawn infrastructure held authoritative telemetry neither
agent could see from inside its own session, and that even harness
telemetry can be a documented partial figure (Condition B's resumed-only
count). Label every cost figure by its evidence class: self-report,
harness-telemetry (full run), or harness-telemetry (partial/resumed —
explicitly not a total). See `method/ENGINEERING-METRICS.md`.

**Stop searching once the applicable checks for the change's classification
pass**, per the Construction Method's stopping rule — do not keep searching
"just in case" once acceptance criteria and the classification's own
checklist are satisfied; this itself re-introduces the read-everything-first
pattern this document exists to caution against.

**Report context honestly.** For a serious experiment, classify reads and
edits as declared-boundary, declared-dependency, undeclared-dependency,
unrelated, or cross-boundary. Do not calculate CER or Discovery Expansion
unless the counting unit and baseline were defined and the events were
captured durably.

## What this document does not yet establish

It does not establish that these rules, followed together, produce a lower
total cost than an unconstrained agent strategy — that is exactly the
open question Experiment 3's own §35 "Recommended Next Experiment"
identifies (a same-mutation, same-architecture trial with the
investigation-strategy variable deliberately controlled). Treat these rules
as the current best operational guidance pending that trial, not as a
proven-optimal strategy.

It also does not establish that feature manifests or smaller Context Surface
reduce cost or defects. Those claims remain `HY-SDE-2026-0007` and
`HY-SDE-2026-0008`, and must be tested rather than inferred from adoption.
