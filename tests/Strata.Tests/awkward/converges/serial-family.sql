-- CONTRACT: Strata must read this the way the server does — the diff after
--           introspecting it back must be EMPTY. See AwkwardFormsTests.fs.
-- NOT-COMPARED: a serial column's default renders as nextval('<table>_<column>_seq'),
--               and the shadow table has a different name, so the two renderings
--               can never match. The diff says so rather than churning forever.
-- Not types. Each becomes an integer type plus a nextval default, and the
-- sequence is the column's, not the project's.
CREATE TABLE t (
    a smallserial,
    b serial,
    c bigserial,
    d serial4,
    e serial8
);
