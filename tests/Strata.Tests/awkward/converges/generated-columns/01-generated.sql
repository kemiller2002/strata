-- CONTRACT: Strata must read this the way the server does — the diff after
--           introspecting it back must be EMPTY, and nothing this case declares
--           may come back not-compared. See AwkwardFormsTests.fs.
--
-- Two things that look alike in a file and are two different states in the
-- catalog. `has_default` in pg_attribute is `atthasdef AND adbin IS NOT NULL` —
-- it asks only whether the column gets a row in pg_attrdef:
--
--   GENERATED  DOES     (the generation expression is stored there)
--   IDENTITY   DOES NOT (its value comes from attidentity)
--
-- Reading GENERATED as having no default made every such column report
-- "default absent in desired state and present in the database" on every run
-- forever (WI-0097). Reading IDENTITY as having one produced the mirror image.
--
-- `serial` is the third member of that family and lives in serial-family.sql,
-- because its nextval default names the table's own sequence and so cannot be
-- compared across schemas at all — a different limitation, kept separate so
-- this case can assert the strong thing.
CREATE TABLE generated_shapes (
    id      integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    width   integer NOT NULL,
    height  integer NOT NULL,
    area    integer GENERATED ALWAYS AS (width * height) STORED,
    label   text NOT NULL DEFAULT 'unlabelled'
);
