#!/usr/bin/env bash
# Catch a stale .ros/work/queue.md (WI-0061).
#
# queue.md is a GENERATED mirror of work-item state, and the upstream `ros`
# binary writes it from the state as it was BEFORE the current mutation is
# applied — so it is always one mutation behind. Complete an item and its row
# still shows the state it had a moment ago; complete the next one and the
# previous row catches up.
#
# That makes the file quietly wrong in exactly the commit that changes it, which
# is the commit someone reads. Nothing else notices: `ros work validate`,
# `ros work backlog-validate` and `ros registry check` all pass while it is
# wrong, because none of them treats the mirror as something to check.
#
# The fix is upstream and queue.md must not be hand-edited, so this does the two
# things that are available: it compares the mirror with the live state in
# .ros/context/current.json, and it prints the one command that refreshes it.
#
# Exit non-zero on a stale row. Intended to run before a commit and in CI.

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

python3 - <<'PY'
import json
import re
import sys

live = {}
for item in json.load(open(".ros/context/current.json")).get("workItems", []):
    live[item["id"]] = item.get("state")

row = re.compile(r"^\|\s*(\S+)\s*\|(?:[^|]*)\|\s*(\S+)\s*\|")
stale = []

for line in open(".ros/work/queue.md"):
    match = row.match(line)
    if not match:
        continue

    identifier, mirrored = match.group(1), match.group(2)

    # A row for an item with no live state is a backlog item that was never
    # started. The mirror is the only record of it, so there is nothing to
    # disagree with.
    if identifier not in live:
        continue

    actual = live[identifier]

    # Only the SETTLED states are checked. An item is `active` for as long as
    # someone is working on it, and the mirror lagging that is harmless — it says
    # `ready`, the work is in progress, and nobody is misled about anything they
    # would act on.
    #
    # `complete` and `blocked` are different. A finished item shown as available
    # invites someone to do it again; a blocked one shown as ready invites them
    # to pick up work that cannot proceed. Those are the rows worth failing over,
    # and narrowing to them is what keeps this check from crying wolf on every
    # commit made mid-item.
    if actual not in ("complete", "blocked"):
        continue

    if actual != mirrored:
        stale.append((identifier, mirrored, actual))

if stale:
    print("QUEUE MIRROR STALE: .ros/work/queue.md disagrees with the live work state", file=sys.stderr)
    print("", file=sys.stderr)
    for identifier, mirrored, actual in stale:
        print(f"  {identifier}: queue.md says '{mirrored}', the work log says '{actual}'", file=sys.stderr)
    print("", file=sys.stderr)
    print("This is WI-0061. The mirror is written one mutation behind, so the item you", file=sys.stderr)
    print("just completed shows its previous state. Refresh it with a no-op update:", file=sys.stderr)
    print("", file=sys.stderr)
    identifier = stale[0][0]
    print(f"  ./ros work update --id {identifier} --occurred-at \"$(date -u +%Y-%m-%dT%H:%M:%SZ)\" \\", file=sys.stderr)
    print(f"      --title \"<the item's existing title>\"", file=sys.stderr)
    print("", file=sys.stderr)
    print("Do NOT hand-edit queue.md: it is generated, and the next mutation overwrites it.", file=sys.stderr)
    sys.exit(1)

print("check-queue-mirror: OK (queue.md matches the live work state)")
PY
