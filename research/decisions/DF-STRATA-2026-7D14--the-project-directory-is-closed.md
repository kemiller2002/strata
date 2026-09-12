---
id: DF-STRATA-2026-7D14
title: A Strata project is a closed directory with an explicit include list, and every mismatch is an error
status: accepted
decision_type: product-architecture
created: 2026-09-12
updated: 2026-09-12
created_by_agent: claude
confidence: medium
supersedes: [DF-STRATA-2026-A4D9]
superseded_by: []
evidence: []
tags: [strata, project-file, manifest, determinism, agents]
---

# Decision Record

## Decision

Strata is given a directory and **owns everything inside it**. `strata.json`
gains a required `include` array listing every object file by explicit path.

> Every `*.sql` file under the project directory must appear in `include`, and
> every path in `include` must exist. Either mismatch **fails the compile**.

No warnings, no severity levels, no policy knob, no override flag.

`DF-STRATA-2026-A4D9` established `strata.json` and the `schema/<name>/<kind>/`
layout; both stand. This supersedes its reliance on a directory walk to
determine project membership, and its `managedSchemas` field (see
`DF-STRATA-2026-C3A2`).

## Context

Membership was previously implicit: Strata walked the tree, so a file present
was a file included. That was defensible while `plan` and `apply` each re-read
the source, because a stray file was still loaded, still diffed, and still
visible in the plan. Compiling to an artifact (`DF-STRATA-2026-2F6B`) changes
what is at stake.

## Rationale

**Input determinism.** An artifact hash is meaningless if the input set is not
pinned. A directory walk depends on whatever the build machine happens to hold —
stray files, editor backups, another branch's leftovers, files ignored by git but
present on disk. `Wire.fs` already guarantees deterministic *output*; the include
list is the missing half.

**The redundancy is the check.** "Strata owns the directory" and "the project
lists its files" together look like belt and braces. They are double-entry
bookkeeping: because both exist and must agree, the *disagreement* is
detectable. A directory walk alone cannot notice that a file was added, because
there is nothing for it to disagree with.

**Inclusion becomes reviewable.** Adding an object becomes a one-line diff in a
file reviewers watch, rather than a new file appearing somewhere in a tree.

**Errors, not warnings.** An automated build ignoring warnings is what a warning
*is*. A human may choose to ignore a report; a pipeline will not notice one.
Since an unresolved file has a cheap resolution — list it, or move it out —
there is no case that needs leniency.

## Consequences

- **There is no `exclude` list.** Moving a file out of the directory is the
  exclusion mechanism, which is simpler and removes a trap: an `exclude` entry
  would read as "leave this object alone" while meaning "this object is now
  undeclared", and an undeclared object under `--allow-drops` is a proposed drop.
- **No globs.** `include: ["schema/**/*.sql"]` is a directory walk wearing a
  manifest's clothes: drop a file and the build takes it. Explicit paths, sorted,
  one per line, so merge conflicts stay trivially resolvable.
- **Scope is `*.sql` only.** `README.md`, `.gitignore` and `strata.json` itself
  are not Strata's business, or the first README breaks the build. Merge and
  editor leftovers (`customer.sql.orig`, `*.sql.bak`) do not match and so do not
  trip it.
- **A listed-but-missing file routes into existing machinery.** The project
  asserts a file that is not there; that is desired state that cannot load,
  which already marks completeness incomplete and suppresses every drop
  project-wide. It must not surface as a bare filesystem error.
- **Reconciliation tooling is required, not optional.** With zero leniency every
  new object needs a manifest line:
  - `strata init --from-tree` — one-time adoption, writes `include` from the
    current tree as a reviewable diff;
  - `strata add <path>` — append one entry;
  - `strata sync` — reconcile, **printing what it would add and requiring
    confirmation**. A `sync` that silently absorbs the tree is the directory walk
    again with extra steps.

## Limitations

This makes inclusion *reviewable*; it does not make it *impossible*. An agent
that can commit `strata.json` can include files. The improvement is that doing so
is a one-line diff in a watched file instead of a new file in a tree, and the
practical guard costs no Strata feature at all: put `strata.json` behind
CODEOWNERS so adding an object needs a named human's approval, and let the agent
write all the SQL it likes.

Prevention, as everywhere else in this design, lives in who holds credentials and
who signs off — not in the tool.
