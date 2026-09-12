-- CONTRACT: Strata must REFUSE this, with a reason that says why. Loading it
--           as something narrower is the bug class this corpus exists to catch.
CREATE TABLE t (id bigint PRIMARY KEY);
REVOKE SELECT ON t FROM strata_awkward_role;
