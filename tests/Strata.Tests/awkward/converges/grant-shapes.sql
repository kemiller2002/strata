-- CONTRACT: Strata must read this the way the server does — the diff after
--           introspecting it back must be EMPTY. See AwkwardFormsTests.fs.
-- GRANT ALL arrives with no privileges list at all. The owner's own privileges
-- are not grants and must not appear on either side.
CREATE TABLE t (id bigint PRIMARY KEY);
GRANT SELECT, INSERT ON t TO strata_awkward_role;
GRANT ALL ON t TO strata_awkward_role2;
