-- CONTRACT: Strata must read this the way the server does — the diff after
--           introspecting it back must be EMPTY. See AwkwardFormsTests.fs.
-- USAGE on the schema is what makes every qualified name inside it reachable,
-- so a project that grants on a table and nothing on its schema has granted
-- nothing usable. The grant is on the scratch schema itself, which is also the
-- one case where the grant's object is not INSIDE a managed schema but IS one.
--
-- GRANT ALL ON SCHEMA arrives with no privileges list, and for a schema ALL
-- means USAGE and CREATE — a different set from a table's.
CREATE TABLE t (id bigint PRIMARY KEY);
GRANT USAGE ON SCHEMA awk TO strata_awkward_role;
GRANT ALL ON SCHEMA awk TO strata_awkward_role2;
GRANT SELECT ON t TO strata_awkward_role;
