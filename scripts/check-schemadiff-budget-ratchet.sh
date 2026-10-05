#!/usr/bin/env bash
# SchemaDiff line budgets only go DOWN (STRATA-QUAL-001, strata#14).
#
# `SchemaDiffArchitectureTests` checks each module against its budget in
# tests/Strata.Tests/architecture/schemadiff-budget.json. That alone would let
# a change raise the budget alongside the code, which is how SchemaDiff.fs grew
# to 3694 lines. So this compares the budget file with the base branch's copy:
#
#   - no module budget may rise unless an unexpired exception in THIS copy
#     names that module and covers the new budget (owner, reason, expires);
#   - the ceiling and the slack may not rise at all.
#
# A module new to the file has no base budget and is limited by the ceiling,
# which the test enforces. A base branch without the file passes: there is
# nothing to ratchet against yet.
#
#   scripts/check-schemadiff-budget-ratchet.sh            # base: $GITHUB_BASE_REF, else main
#   scripts/check-schemadiff-budget-ratchet.sh release/1  # base: release/1
set -euo pipefail
cd "$(dirname "$0")/.."

base="${1:-${GITHUB_BASE_REF:-main}}"
path="tests/Strata.Tests/architecture/schemadiff-budget.json"

git fetch --quiet --no-tags --depth=1 origin "$base"

if ! base_json="$(git show "FETCH_HEAD:$path" 2>/dev/null)"; then
    echo "check-schemadiff-budget-ratchet: OK ($path is new relative to $base)"
    exit 0
fi

BASE_JSON="$base_json" python3 - "$path" "$base" <<'PY'
import datetime
import json
import os
import sys

path, base_name = sys.argv[1], sys.argv[2]
head = json.load(open(path))
base = json.loads(os.environ["BASE_JSON"])
today = datetime.datetime.now(datetime.timezone.utc).date()

def covered(module, budget):
    for e in head.get("exceptions", []):
        if (e.get("module") == module
                and e.get("owner", "").strip()
                and e.get("reason", "").strip()
                and datetime.date.fromisoformat(e["expires"]) >= today
                and e.get("budget", 0) >= budget):
            return True
    return False

problems = []
for key in ("ceiling", "slack"):
    if head[key] > base[key]:
        problems.append(f"{key} rose from {base[key]} to {head[key]}; it may only fall")

for module, budget in sorted(head["modules"].items()):
    before = base["modules"].get(module)
    if before is not None and budget > before and not covered(module, budget):
        problems.append(
            f"{module} budget rose from {before} to {budget} with no unexpired exception covering it; "
            "split the module, or add an exception with an owner, a reason and an expiry date")

if problems:
    print(f"SCHEMADIFF BUDGET RATCHET: budgets may only fall relative to {base_name}", file=sys.stderr)
    for p in problems:
        print(f"  {p}", file=sys.stderr)
    sys.exit(1)

print(f"check-schemadiff-budget-ratchet: OK ({len(head['modules'])} module budget(s) did not rise relative to {base_name})")
PY
