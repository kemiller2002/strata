-- A policy expression may contain its own table's name AS DATA. Creating this
-- in the shadow schema means rewriting the table name, and that was done with a
-- plain String.Replace — which rewrote the literal too, changing what the policy
-- means. The shadow then rendered an expression the file never declared, the two
-- compared unequal, and Strata proposed replace-policy on every run (WI-0094).
--
-- The name also appears in this comment, twice above, for the same reason.
CREATE POLICY doc_tenant ON awk.doc
    USING (tenant = 'acme' AND note <> 'see awk.doc for details');
