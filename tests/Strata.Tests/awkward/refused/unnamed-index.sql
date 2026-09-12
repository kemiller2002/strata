-- CONTRACT: Strata must REFUSE this, with a reason that says why. Loading it
--           as something narrower is the bug class this corpus exists to catch.
-- An unnamed index gets a server-generated name a file cannot predict.
CREATE TABLE t (id bigint PRIMARY KEY, a int);
CREATE INDEX ON t (a);
