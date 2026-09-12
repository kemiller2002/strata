---
id: DF-STRATA-2026-5E9F
title: A potentially destructive change never receives an Allow verdict, whatever the corpus found
status: accepted
decision_type: architecture
created: 2026-09-12
updated: 2026-09-12
created_by_agent: claude
confidence: high
supersedes: []
superseded_by: []
evidence: []
tags: [strata, gate, destructive, analysis-scope, er-008]
---

# Decision Record

## Decision

Every change for which `Change.isPotentiallyDestructive` is true is classified
`Block` or `RequiresApproval`. **Never `Allow`.** The corpus decides *which* of
those two, never whether to ask the question at all.

| corpus result | verdict |
|---|---|
| dependents found | `Block` — known breakage, not a judgement call |
| none found, scope supports the claim | `RequiresApproval` — "nothing in the indexed scope depends on this" |
| none found, scope does not support it | `RequiresApproval` — "this is not clearance" |

This is enforced by a **property test over the whole vocabulary**, not by
convention: for every `Change` case, `isPotentiallyDestructive c` implies the
verdict is not `Allow`.

## Context

This did not hold. `cleanResultVerdict` in `DeploymentGate` was:

```fsharp
let cleanResultVerdict =
    if scopeSupportsAbsence then Allow else RequiresApproval
```

so `DropColumn`, `DropTable` and `AlterColumnType` reached `Allow` whenever a
corpus had been indexed and found nothing. `RenameTable`, `RenameColumn`,
`ReplaceView`, `ReplaceRoutine`, `CreateTrigger`, `DropTrigger`, `ReplaceTrigger`
and `UpdateRow` did the same through their own `if List.isEmpty … && scopeSupportsAbsence`
conditions. Every one of those is in `isPotentiallyDestructive`.

## Rationale

`scopeSupportsAbsence` proves the search was **meaningful** — a corpus was
indexed, so "not found" is not vacuous. It does not prove the search was
**complete**. Strata cannot see dynamic SQL built from strings, ORM-generated
queries, a BI tool, another service, a cron job, or a notebook.

The codebase knows this better than most: the entire analysis-scope apparatus
exists to keep bounded results bounded, and every answer carries its scope. Then
at the final step, for `DROP TABLE`, it promoted *"I searched what I was pointed
at and found nothing"* into clearance. That was the one place in the design where
a bounded result became an unbounded conclusion — the exact collapse `ER-008`
exists to forbid, sitting at the highest-stakes change in the vocabulary.

The asymmetry settles it. Requiring approval on a safe drop costs a keystroke;
auto-approving an unsafe one costs a table.

## Consequences

- Approval fatigue is the fair objection and it does not bite here. Drops already
  require `--allow-drops`, so they are deliberate and rare; the fatigue argument
  is about `AddColumn`, which is unaffected.
- `cleanResultRationale` and `cleanResultNextMove` keep their two forms — the
  distinction between "the scope supports this conclusion" and "this is not
  clearance" is still worth stating, it just no longer decides the verdict.
- The property test is the durable part. Six change cases were added to this
  vocabulary in a single session; nothing would have caught one of them being
  misclassified.
