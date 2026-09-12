-- CONTRACT: Strata must REFUSE this, with a reason that says why. Loading it
--           as something narrower is the bug class this corpus exists to catch.
-- `ALL TABLES IN SCHEMA` is resolved at execution time against whatever exists
-- then, so it does not describe a state a file can hold. A table added later is
-- not covered, which makes it a one-off operation dressed as a declaration.
GRANT SELECT ON ALL TABLES IN SCHEMA awk TO strata_awkward_role;
