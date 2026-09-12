-- CONTRACT: Strata must read this the way the server does — the diff after
--           introspecting it back must be EMPTY. See AwkwardFormsTests.fs.
--
-- A policy with no FOR clause is FOR ALL, and the parse tree carries no
-- cmd_name at all rather than "all". A policy with no TO clause is TO PUBLIC,
-- which arrives as a ROLESPEC_PUBLIC entry rather than an empty list.
--
-- A SELECT policy has no WITH CHECK and an INSERT policy has no USING; both
-- absences are real states and neither is an empty expression.
CREATE TABLE t (id bigint PRIMARY KEY, tenant text, body text);

CREATE POLICY p_default ON t USING (tenant = 'acme');
CREATE POLICY p_select ON t FOR SELECT TO PUBLIC USING (body IS NOT NULL);
CREATE POLICY p_insert ON t FOR INSERT WITH CHECK (tenant IS NOT NULL);
CREATE POLICY p_restrictive ON t AS RESTRICTIVE FOR ALL TO strata_awkward_role
    USING (tenant = 'acme') WITH CHECK (tenant = 'acme');

ALTER TABLE t ENABLE ROW LEVEL SECURITY;
