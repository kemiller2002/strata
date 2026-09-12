#!/usr/bin/env bash
# Run ROS validation. CI runs THIS script, so a local check and CI cannot drift.
#
# `./ros validate` on its own checks the working tree. With ROS_BASE_REF set it
# widens the check to every file changed since that commit, which is what
# catches work-item attribution for files committed earlier in a branch.
#
# The two disagree silently in the direction that matters — a local run passes
# while CI reports files with no attribution — so run this before pushing rather
# than `./ros validate` alone.
#
#   scripts/ros-validate.sh              # against origin/main
#   scripts/ros-validate.sh origin/dev   # against another branch
#   scripts/ros-validate.sh <sha>        # against an explicit commit, as CI does
set -euo pipefail
cd "$(dirname "$0")/.."

BASE="${1:-origin/main}"

# Fetched rather than assumed present: a fresh clone or a stale remote ref makes
# the merge base wrong, and a wrong base silently NARROWS what gets checked.
# A bare SHA has nothing to fetch, so a failure here is not an error.
git fetch --quiet origin "${BASE#origin/}" 2>/dev/null || true

# The merge base rather than the base ref itself. Validating against the base
# branch's current tip would flag files that changed on the base since this
# branch left it — other people's commits, reported as this branch's missing
# attribution.
if ! MERGE_BASE=$(git merge-base "$BASE" HEAD 2>/dev/null); then
    echo "cannot find a merge base with ${BASE}." >&2
    echo "Pass a base explicitly: scripts/ros-validate.sh <ref-or-sha>" >&2
    exit 1
fi

echo "validating against ${BASE} (merge base ${MERGE_BASE})"

# Attribution comes from a work event's `paths`, which ros fills from the DIRTY
# TREE when a work item completes. So a file committed BEFORE its work item
# completed carries no attribution, and nothing after the fact can supply it —
# not an evidence path, not `work attach`, which copies files as artifacts.
# Complete work while the change is still in the tree.
ROS_BASE_REF="$MERGE_BASE" ./ros validate
./ros registry check
./ros artifacts validate

# queue.md is a generated mirror that none of the checks above look at, and it
# is written one mutation behind (WI-0061) — so it is quietly wrong in exactly
# the commit that changes it, which is the commit someone reads.
scripts/check-queue-mirror.sh
