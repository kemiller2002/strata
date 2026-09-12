-- CONTRACT: Strata must read this the way the server does — the diff after
--           introspecting it back must be EMPTY. See AwkwardFormsTests.fs.
-- The parser's spelling and the catalog's differ for most of these.
CREATE TABLE t (
    a int8, b int4, int2_col int2, d float8, e float4, f bool,
    g varchar(50), h bpchar(4), i timestamptz, j timestamp,
    k timetz, l time, m numeric(12,2), n numeric, o text
);
