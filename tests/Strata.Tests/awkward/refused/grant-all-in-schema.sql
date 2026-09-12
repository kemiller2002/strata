-- `ALL TABLES IN SCHEMA` is resolved at execution time against whatever exists
-- then, so it does not describe a state a file can hold. A table added later is
-- not covered, which makes it a one-off operation dressed as a declaration.
GRANT SELECT ON ALL TABLES IN SCHEMA awk TO strata_awkward_role;
