#!/usr/bin/env bash
# Catch a resolved field that reaches nobody (WI-0100).
#
# `SchemaDiff.Inputs` is built with a RECORD-UPDATE expression — `{ Inputs.between
# a b with ... }` — at every call site. That is deliberate: most of its fields
# default to the state that proposes the least, so a caller supplying nothing
# gets silence rather than a confident wrong answer.
#
# The cost is that F# cannot tell you when a NEW field is never supplied. It
# takes the default and everything compiles, so the diff runs with the field
# empty and reports every affected object as not-compared. That is what happened
# when `NormalisedDomains` was added: `Resolution` filled it, `Artifact` carried
# it, the diff read it, and `Deployment` — the one place `strata plan` goes
# through — never passed it on. `plan` reported a domain it had every fact about
# as one it could not compare.
#
# So: every field that `ResolvedDesiredState` and `SchemaDiff.Inputs` BOTH have
# must be named in each of the files that build an `Inputs` from a resolved
# state. Same-named on both types is what makes a field this script's business;
# a field on only one of them is not a wiring question.
#
# Exit non-zero when one is missing. Intended to run before a commit and in CI.

set -euo pipefail
cd "$(dirname "$0")/.."

python3 - <<'PY'
import re
import sys

def fields(path, start):
    """Field names declared in the record type whose definition starts at `start`."""
    text = open(path).read()
    body = text[text.index(start) + len(start):]
    found = []
    for line in body.splitlines():
        stripped = line.strip()
        # The record ends at its closing brace on a line of its own content.
        if stripped.endswith("}") and found:
            match = re.match(r"^([A-Z]\w*)\s*:", stripped)
            if match:
                found.append(match.group(1))
            break
        match = re.match(r"^\{?\s*([A-Z]\w*)\s*:", stripped)
        if match:
            found.append(match.group(1))
    return found

resolved = fields("src/Strata.Application/ResolvedDesiredState.fs", "type ResolvedDesiredState =")
inputs = fields("src/Strata.Application/SchemaDiff.fs", "    type Inputs =")

shared = [f for f in resolved if f in inputs]

if not shared:
    print("check-resolved-state-wiring: could not read either record's fields", file=sys.stderr)
    sys.exit(1)

# Every place that builds an Inputs from a ResolvedDesiredState. The corpus
# harness is here for the same reason the CLI is: it exists to notice a
# comparison that silently did not happen (WI-0093), and it cannot notice one it
# never asked for.
consumers = [
    "src/Strata.Cli/Deployment.fs",
    "tests/Strata.Tests/AwkwardFormsTests.fs",
]

missing = []
for consumer in consumers:
    text = open(consumer).read()
    for field in shared:
        if not re.search(r"\b%s\s*=" % re.escape(field), text):
            missing.append((consumer, field))

if missing:
    print("RESOLVED STATE NOT WIRED: a field the diff reads is never supplied", file=sys.stderr)
    print("", file=sys.stderr)
    for consumer, field in missing:
        print(f"  {consumer} never sets {field}", file=sys.stderr)
    print("", file=sys.stderr)
    print("`SchemaDiff.Inputs` is built with a record-update expression, so a field", file=sys.stderr)
    print("nobody sets takes its default and compiles. The diff then reports every", file=sys.stderr)
    print("object that field describes as not-compared, and nothing fails.", file=sys.stderr)
    sys.exit(1)

print(f"check-resolved-state-wiring: OK ({len(shared)} resolved field(s) reach every consumer)")
PY
