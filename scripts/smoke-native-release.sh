#!/usr/bin/env bash
# Smoke-test a packaged, self-contained Strata executable before it is published:
#
#   scripts/smoke-native-release.sh <strata-executable> <version> <commit>
#
# 1. Its `version` identity is exactly the release (Echelon repository lifecycle
#    contract v1): system id, repository, executable, version, source commit.
# 2. init/verify/status/doctor succeed on a clean repository, and a second init
#    and an upgrade change no file.
# 3. With STRATA_SMOKE_PG set, it compiles a one-table project against that
#    server and validates SQL against the artifact. That exercises the bundled
#    native PostgreSQL parser (libpg_query), which is the part of a
#    self-contained single-file build most likely to be missing.
set -euo pipefail
exe="$1"; version="$2"; commit="$3"

STRATA_IDENTITY=$("$exe" version) python3 - "$version" "$commit" <<'PY'
import json, os, sys
version, commit = sys.argv[1], sys.argv[2]
doc = {k.lower(): v for k, v in json.loads(os.environ["STRATA_IDENTITY"]).items()}
expected = {"systemid": "strata", "repository": "kemiller2002/strata", "executable": "strata",
            "releaseversion": version, "sourcecommit": commit}
wrong = {k: (doc.get(k), v) for k, v in expected.items() if doc.get(k) != v}
if wrong:
    sys.exit(f"version identity mismatch: {wrong}")
print(f"identity ok: strata {version} @ {commit}")
PY

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
root="$work/repository"
mkdir -p "$root"
snapshot() { (cd "$root" && find . -type f -print0 | sort -z | xargs -0 sha256sum); }

if "$exe" verify --root "$root" >/dev/null 2>&1; then
  echo "verify passed on a repository Strata was never installed in" >&2; exit 1
fi
"$exe" init --root "$root" >/dev/null
"$exe" verify --root "$root" >/dev/null
"$exe" status --root "$root" >/dev/null
"$exe" doctor --root "$root" >/dev/null
first=$(snapshot)

second_init=$("$exe" init --root "$root")
upgrade=$("$exe" upgrade --root "$root")
"$exe" verify --root "$root" >/dev/null
[ "$first" = "$(snapshot)" ] || { echo "second application changed repository state" >&2; exit 1; }
python3 - "$second_init" "$upgrade" <<'PY'
import json, sys
for label, text in zip(("init", "upgrade"), sys.argv[1:]):
    outcomes = {f["path"]: f["outcome"] for f in json.loads(text)["files"]}
    if any(o != "unchanged" for o in outcomes.values()):
        sys.exit(f"second {label} reported changes: {outcomes}")
print("lifecycle ok: second application changed nothing")
PY

if [ -n "${STRATA_SMOKE_PG:-}" ]; then
  project="$work/project"
  mkdir -p "$project/schema/smoke/tables"
  echo "CREATE TABLE smoke.thing (id bigint PRIMARY KEY, label text NOT NULL);" > "$project/schema/smoke/tables/thing.sql"
  "$exe" init --project "$project" >/dev/null
  "$exe" compile --project "$project" --out "$work/smoke.artifact" --connection "$STRATA_SMOKE_PG"
  echo "SELECT label FROM smoke.thing;" > "$work/valid.sql"
  echo "SELECT missing_column FROM smoke.thing;" > "$work/invalid.sql"
  "$exe" validate "$work/valid.sql" --artifact "$work/smoke.artifact"
  set +e
  "$exe" validate "$work/invalid.sql" --artifact "$work/smoke.artifact"
  status=$?
  set -e
  [ "$status" -eq 1 ] || { echo "validate of a wrong column exited $status, not 1" >&2; exit 1; }
  echo "parser ok: compile and offline validation work from the packaged executable"
else
  echo "STRATA_SMOKE_PG not set: the parser smoke was not run" >&2
fi
