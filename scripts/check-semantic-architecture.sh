#!/usr/bin/env bash
# Architecture check for DF-STRATA-2026-D3F8.
#
# Verifies the four-tier dependency direction mechanically rather than by
# review. The load-bearing rule is that Tier 1 (Strata.Semantic) references
# ONLY FSharp.Core, so it structurally cannot reach the parser, protobuf, or a
# database driver. That is the enforcement of ER-014 and D-005: the semantic
# model is smaller than the parser AST and independent of it.
#
# Exit non-zero on violation. Intended to run in CI and before work-item
# completion.

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

failures=0

fail() {
    echo "ARCHITECTURE VIOLATION: $*" >&2
    failures=$((failures + 1))
}

# --- Tier 1: Strata.Semantic ------------------------------------------------

semantic="src/Strata.Semantic/Strata.Semantic.fsproj"

if [[ ! -f "$semantic" ]]; then
    fail "$semantic not found"
else
    if grep -q "<PackageReference" "$semantic"; then
        fail "Strata.Semantic has a PackageReference; Tier 1 may reference only FSharp.Core"
        grep -n "<PackageReference" "$semantic" >&2
    fi

    if grep -q "<ProjectReference" "$semantic"; then
        fail "Strata.Semantic has a ProjectReference; Tier 1 depends on nothing"
        grep -n "<ProjectReference" "$semantic" >&2
    fi

    if ! grep -q "<TargetFramework>netstandard2.0</TargetFramework>" "$semantic"; then
        fail "Strata.Semantic must target netstandard2.0 to constrain what it can reference"
    fi
fi

# --- Tier 2: Strata.Analysis ------------------------------------------------

analysis="src/Strata.Analysis/Strata.Analysis.fsproj"

if [[ ! -f "$analysis" ]]; then
    fail "$analysis not found"
else
    if grep -q "<PackageReference" "$analysis"; then
        fail "Strata.Analysis has a PackageReference; Tier 2 consumes Strata-owned types only"
        grep -n "<PackageReference" "$analysis" >&2
    fi

    for forbidden in "Strata.Host" "Strata.Application" "Strata.Cli"; do
        if grep -q "$forbidden" "$analysis"; then
            fail "Strata.Analysis references $forbidden; dependencies point downward only"
        fi
    done
fi

# --- Parser AST containment -------------------------------------------------
#
# The protobuf AST must not appear in Tier 1 or Tier 2 source at all. This is
# the textual counterpart to the project-reference rules above: it catches a
# type leaking through a signature even if the build graph still happens to
# allow it.

for tier_src in src/Strata.Semantic src/Strata.Analysis; do
    if [[ -d "$tier_src" ]]; then
        if grep -rn --include="*.fs" -E "PgSqlParser|Google\.Protobuf|Npgsql" "$tier_src" >/dev/null 2>&1; then
            fail "parser/driver types appear in $tier_src; they must stay behind the Tier 4 adapter"
            grep -rn --include="*.fs" -E "PgSqlParser|Google\.Protobuf|Npgsql" "$tier_src" >&2
        fi
    fi
done

# --- Tier 3: Strata.Application ---------------------------------------------
#
# Orchestration composes; it must not acquire a host dependency of its own.

app="src/Strata.Application/Strata.Application.fsproj"

if [[ -f "$app" ]]; then
    if grep -q "<PackageReference" "$app"; then
        fail "Strata.Application has a PackageReference; Tier 3 composes Strata's own tiers"
        grep -n "<PackageReference" "$app" >&2
    fi

    if grep -q "Strata.Host" "$app"; then
        fail "Strata.Application references a host adapter; Tier 3 must not perform I/O"
    fi
fi

if [[ -d "src/Strata.Application" ]]; then
    if grep -rn --include="*.fs" -E "PgSqlParser|Google\.Protobuf|Npgsql" "src/Strata.Application" >/dev/null 2>&1; then
        fail "parser/driver types appear in Strata.Application; they belong behind Tier 4"
    fi
fi

# --- Tier 4 driver containment ----------------------------------------------
#
# Npgsql belongs to the catalog adapter alone; the parser adapter must not
# acquire a database dependency, and vice versa.

if [[ -f "src/Strata.Host.PgParser/Strata.Host.PgParser.fsproj" ]]; then
    if grep -q "Npgsql" "src/Strata.Host.PgParser/Strata.Host.PgParser.fsproj"; then
        fail "Strata.Host.PgParser references Npgsql; the parser adapter does not talk to a database"
    fi
fi

if [[ -f "src/Strata.Host.Postgres/Strata.Host.Postgres.fsproj" ]]; then
    if grep -qE "pgsqlparser|Google\.Protobuf" "src/Strata.Host.Postgres/Strata.Host.Postgres.fsproj"; then
        fail "Strata.Host.Postgres references the parser; catalog introspection does not parse SQL"
    fi
fi

# --- Result -----------------------------------------------------------------

if [[ $failures -gt 0 ]]; then
    echo "" >&2
    echo "check-semantic-architecture: $failures violation(s)" >&2
    exit 1
fi

echo "check-semantic-architecture: OK (four-tier dependency direction holds)"
