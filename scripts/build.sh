#!/usr/bin/env bash
# Build the solution in Release.
#
# `dotnet build Strata.sln` alone produces a Debug build: the solution
# metaproject passes Configuration=Debug as a global property, which
# Directory.Build.props cannot override. Every timing, benchmark and published
# binary goes through this script so the configuration is never implicit.
set -euo pipefail
cd "$(dirname "$0")/.."
exec dotnet build Strata.sln -c Release "$@"
