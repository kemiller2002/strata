-- The same shape as the column-level refusal. WITH GRANT OPTION lets the
-- grantee pass the privilege on to anyone; the model holds the privilege and
-- not that power, so reading this as a plain grant would have the plan say
-- "grants SELECT to a role" while that role can hand SELECT to the world.
CREATE TABLE t (id bigint PRIMARY KEY);
GRANT SELECT ON t TO strata_awkward_role WITH GRANT OPTION;
