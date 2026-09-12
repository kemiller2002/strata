#!/usr/bin/env bash
# Build the database the live integration tests need, and print its connection
# string.
#
# Without this the 17 tests in CatalogIntrospectionTests SKIP rather than run,
# which looks like a green suite while testing nothing (WI-0053).
#
#   scripts/test-fixture.sh                      # builds strata_test
#   scripts/test-fixture.sh my_db                # builds my_db
#
# Needs a superuser connection: it creates a role and a database. Set PGHOST /
# PGPORT / PGUSER as usual, or run it where `psql` reaches a local server as a
# superuser. DROPS AND RECREATES the named database.
set -euo pipefail
cd "$(dirname "$0")/.."

DB="${1:-strata_test}"
ROLE="${STRATA_TEST_ROLE:-strata}"
PASSWORD="${STRATA_TEST_PASSWORD:-strata_local_test}"

# The test role must NOT be a superuser: a superuser can read the `hidden`
# schema, and three tests exist precisely because the connected role cannot.
psql -v ON_ERROR_STOP=1 -q -d postgres <<SQL
DO \$\$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '${ROLE}') THEN
        CREATE ROLE ${ROLE} LOGIN PASSWORD '${PASSWORD}';
    ELSE
        ALTER ROLE ${ROLE} LOGIN NOSUPERUSER PASSWORD '${PASSWORD}';
    END IF;
END
\$\$;
SQL

# Owned by the test role, so `SET ROLE` in the fixture can create schemas. The
# superuser still creates the `hidden` schema, which is what keeps it out of
# the test role's reach.
psql -v ON_ERROR_STOP=1 -q -d postgres -c "DROP DATABASE IF EXISTS ${DB}"
psql -v ON_ERROR_STOP=1 -q -d postgres -c "CREATE DATABASE ${DB} OWNER ${ROLE}"
psql -v ON_ERROR_STOP=1 -q -d "${DB}" -f scripts/test-fixture.sql

echo "Fixture ready. Run the live tests with:"
echo
echo "  STRATA_TEST_PG=\"Host=localhost;Port=\${PGPORT:-5432};Database=${DB};Username=${ROLE};Password=${PASSWORD}\" \\"
echo "    dotnet test Strata.sln -c Release"
