-- CONTRACT: Strata must REFUSE this, with a reason that says why. Loading it
--           as something narrower is the bug class this corpus exists to catch.
CREATE TABLE t (id bigint PRIMARY KEY, at timestamptz);
INSERT INTO t (id, at) VALUES (1, now());
