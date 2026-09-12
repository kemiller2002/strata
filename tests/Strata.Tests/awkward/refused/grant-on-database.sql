-- A database is not in any schema, so it lies outside everything Strata's
-- managed-schema rule can reason about. Refused by kind rather than read as
-- some relation of that name.
GRANT CONNECT ON DATABASE strata_test TO strata_awkward_role;
