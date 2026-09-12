---
id: DF-STRATA-2026-9A41
title: An artifact carries desired state only, and never a fact about a database
status: accepted
decision_type: product-architecture
created: 2026-09-12
updated: 2026-09-12
created_by_agent: claude
confidence: high
supersedes: []
superseded_by: []
evidence: []
tags: [strata, artifact, compile, deployment, reference-data, er-008]
---

# Decision Record

## Decision

A compiled artifact carries **desired state and nothing else**. Any value whose
derivation read a live database's *contents or schema* is excluded, even when
Strata already has it in hand and excluding it means computing it twice.

Concretely, `ResolvedDesiredState` is not uniformly compilable, and the artifact
format splits it:

| Field | In the artifact? | Why |
| --- | --- | --- |
| `Declared` (the whole model) | yes | The project, parsed. A function of the source. |
| `NormalisedViews`, `NormalisedTables`, `Policies` | yes | The DECLARED side rendered by *a* server. Depends on the source and on a PostgreSQL, not on any target. |
| `CompiledWith` | yes | Which server did that rendering. Needed to refuse a target that would render differently. |
| `Warnings` | yes | The conditions the compile ran under. |
| `Data`, `DataFailures` | **no** | Half of a resolved row is *what the target already holds*. |

`deploy` resolves reference rows against its own target, from the declared
literal tokens the artifact does carry.

## Why

`DF-STRATA-2026-2F6B` drew the line at "depends on the target at the moment of
deployment". `ReferenceData.resolve` sits on both sides of that line in one
function: it renders the declared literals through the real column types — which
is desired state, and compilable — and it reads the rows the table currently
holds, which is not.

The distinction is easy to lose because the two arrive together, and losing it is
not loud. An artifact compiled against a database that already held the reference
rows recorded *"declared and deployed agree"*. Deploying it to an empty database
created the table and inserted **nothing**: four statements instead of six, exit
0, a clean-looking run, and a lookup table with no rows in it.

Nothing in that output was false. The artifact was a correct answer to a question
about a *different server*. That is the failure mode this record exists to
prevent, and it is the same shape as every `ER-008` violation in this codebase:
a value that is right about one thing being read as though it were right about
another.

## The rule, stated so it can be applied to the next field

Before a field goes into the artifact, ask: **if this artifact were deployed to a
database other than the one it was compiled against, would this value still be
true?**

- `'open'::text` — yes. It is what PostgreSQL 16 makes of `DEFAULT 'open'`, and
  the version check guarantees the target is PostgreSQL 16.
- "the table currently holds rows 1 and 2" — no. It was never a claim about the
  project.

A value that fails the question is recomputed at deploy time or not used.

## Consequences

- **Reference rows are resolved twice** for a compile-then-deploy, once at each
  end. That is the cost, and it is the right one: the compile-time resolution is
  what makes `compile` able to report a data failure early, and the deploy-time
  one is the only one that can be correct for the target.
- **`Data` and `DataFailures` read back empty** from an artifact, and the caller
  must fill them. Tested, so a future change that starts carrying them fails.
- **`strata drift` (`WI-0086`) inherits the rule.** Drift compares a server
  against an artifact; the artifact's reference rows would be the wrong ones for
  the same reason, so drift resolves against the server it is checking.
- **Signing (`WI-0089`) gets easier.** A signature over an artifact that carried
  target state would change when the target changed, which is not a property
  anyone wants from a signature.

## Alternatives rejected

- **Carry the resolved rows and let deploy ignore them.** Then the format has a
  field that is present, plausible, and wrong — the most expensive kind. A reader
  writing `drift` or a debugging tool would use it.
- **Carry only the declared half of each resolution.** Closer, but it still bakes
  in a rendering through *the compiling server's* column types for a table the
  target may define differently after this very deployment. The declared literal
  tokens are what the author wrote, and re-rendering them at the target is both
  simpler and more faithful.
