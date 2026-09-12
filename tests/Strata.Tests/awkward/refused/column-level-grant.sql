-- Parses with the same RangeVar as a table-wide grant. Reading it as one hands
-- out access to columns the file withheld.
CREATE TABLE t (id bigint PRIMARY KEY, secret text);
GRANT SELECT (id) ON t TO strata_awkward_role;
