#!/usr/bin/env bash
# Catch documentation that has drifted from the CLI or from the tree (DOC-005,
# DOC-006 in docs/strata/DOCUMENTATION-REQUIREMENTS.md).
#
# README.md and docs/strata/AGENT-GUIDE.md are full of copy-pasteable commands.
# A flag that is renamed or a command that is removed leaves those examples
# silently wrong — and a wrong example is worse than no example, because someone
# pastes it and gets an error they then have to debug against the docs that
# caused it.
#
# So: every `strata <command>` and every `--flag` the docs mention must appear in
# the CLI's own `--help` (DOC-005), and every relative link must point at a file
# that exists (DOC-006). This checks the SHAPE of the documentation, not its
# prose; it cannot tell you an explanation is out of date, that a transcript was
# invented, or that a figure was recalled rather than measured. Those are
# DOC-001 to DOC-004 and DOC-007 to DOC-008, and they stay human obligations.
#
# Exit non-zero on a command or flag the CLI does not have, or a broken link.

set -euo pipefail
cd "$(dirname "$0")/.."

HELP_FILE="$(mktemp)"
trap 'rm -f "$HELP_FILE"' EXIT

if ! dotnet run --project src/Strata.Cli -c Release --no-build -- --help >"$HELP_FILE" 2>/dev/null; then
    echo "check-docs-match-cli: could not run the CLI; build it first with scripts/build.sh" >&2
    exit 1
fi

HELP_FILE="$HELP_FILE" python3 - <<'PY'
import os
import re
import sys

help_text = open(os.environ["HELP_FILE"]).read()

cli_flags = set(re.findall(r"--[a-z][a-z-]*", help_text))
# Commands are the first word of each COMMANDS entry, which is indented two
# spaces in the help output.
cli_commands = set(re.findall(r"^  ([a-z][a-z-]*)\s", help_text, re.M))

docs = [
    "README.md",
    "docs/strata/AGENT-GUIDE.md",
    "docs/strata/DOCUMENTATION-REQUIREMENTS.md",
]

# Flags that belong to `dotnet`, not to strata, and markdown rules that look
# like flags. Listed rather than pattern-matched so a new one is a deliberate
# addition.
NOT_STRATA_FLAGS = {"--help", "--no-build", "--project", "---"}

problems = []

for path in docs:
    text = open(path).read()

    # Only look inside fenced code blocks: prose may legitimately name a flag
    # in passing, and evidence-record FILENAMES contain `--` sequences that are
    # not flags at all.
    #
    # Walked line by line rather than matched with one regex. A regex that only
    # recognises ```bash pairs the CLOSING fence of a ```mermaid block with the
    # next opening fence and swallows the prose between them — which is how the
    # first version of this check reported five evidence-record filenames as
    # undefined flags.
    blocks, current, language = [], None, None
    for line in text.splitlines():
        if line.startswith("```"):
            if current is None:
                current, language = [], line[3:].strip().lower()
            else:
                if language in ("", "bash", "sh", "console", "text"):
                    blocks.append("\n".join(current))
                current, language = None, None
        elif current is not None:
            current.append(line)

    code = "\n".join(blocks)

    for flag in sorted(set(re.findall(r"--[a-z][a-z-]*", code))):
        if flag in NOT_STRATA_FLAGS or flag in cli_flags:
            continue
        problems.append(f"{path}: documents flag {flag}, which the CLI does not have")

    for command in sorted(set(re.findall(r"\bstrata ([a-z][a-z-]*)", code))):
        if command not in cli_commands:
            problems.append(f"{path}: documents command `strata {command}`, which the CLI does not have")

    # DOC-006: every relative link resolves. A link into research/ or docs/ that
    # points at a renamed record is the same failure as a renamed flag — the
    # reader follows it and lands nowhere.
    base = os.path.dirname(path)
    for label, target in re.findall(r"\[([^\]]+)\]\(([^)#\s]+)(?:#[^)]*)?\)", text):
        if target.startswith(("http://", "https://", "mailto:")):
            continue
        if not os.path.exists(os.path.normpath(os.path.join(base, target))):
            problems.append(f"{path}: link [{label}] points at {target}, which does not exist")

if problems:
    print("DOCS DO NOT MATCH THE CLI", file=sys.stderr)
    print("", file=sys.stderr)
    for problem in problems:
        print(f"  {problem}", file=sys.stderr)
    print("", file=sys.stderr)
    print("A copy-pasteable example that no longer works, or a link that lands", file=sys.stderr)
    print("nowhere, is worse than none. Update the docs, or restore the target.", file=sys.stderr)
    sys.exit(1)

print(
    f"check-docs-match-cli: OK ({len(docs)} document(s) — every command, flag "
    f"and link resolves)"
)
PY
