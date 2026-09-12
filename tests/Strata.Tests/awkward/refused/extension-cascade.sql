-- CONTRACT: Strata must REFUSE this, with a reason that says why. Loading it
--           as something narrower is the bug class this corpus exists to catch.
--
-- CASCADE installs whatever the extension requires, which is a decision about
-- objects the file never names. Read as a plain CREATE EXTENSION it would bring
-- in dependencies nobody declared.
CREATE EXTENSION postgis CASCADE;
