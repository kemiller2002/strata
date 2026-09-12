-- CONTRACT: Strata must read this the way the server does — the diff after
--           introspecting it back must be EMPTY. See AwkwardFormsTests.fs.
--
-- Column privileges live in pg_attribute.attacl, a SEPARATE store from the
-- table's relacl rather than a narrower view of it. A column grant writes
-- nothing into relacl and a table grant writes nothing into attacl.
--
-- One statement can mix the two scopes, and ALL means four privileges on a
-- column against seven on a table. A role granted a column privilege and
-- nothing else has no table privilege at all.
CREATE TABLE t (id bigint PRIMARY KEY, total numeric(12,2), note text);

GRANT SELECT (id, total) ON t TO strata_awkward_role;
GRANT SELECT (note), INSERT ON t TO strata_awkward_role2;
GRANT ALL (note) ON t TO strata_awkward_role;
