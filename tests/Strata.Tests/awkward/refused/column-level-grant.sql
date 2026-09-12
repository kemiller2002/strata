-- CONTRACT: Strata must REFUSE this, with a reason that says why. Loading it
--           as something narrower is the bug class this corpus exists to catch.
-- Parses with the same RangeVar as a table-wide grant. Reading it as one hands
-- out access to columns the file withheld.
CREATE TABLE t (id bigint PRIMARY KEY, secret text);
GRANT SELECT (id) ON t TO strata_awkward_role;
