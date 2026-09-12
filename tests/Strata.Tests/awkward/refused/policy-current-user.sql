-- CONTRACT: Strata must REFUSE this, with a reason that says why. Loading it
--           as something narrower is the bug class this corpus exists to catch.
--
-- CURRENT_USER names whoever happens to be connected, which a file cannot fix
-- and a diff cannot compare. Read as PUBLIC it would admit every role.
CREATE TABLE t (id bigint PRIMARY KEY, tenant text);
CREATE POLICY p ON t FOR SELECT TO CURRENT_USER USING (tenant = 'acme');
