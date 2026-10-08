-- CONTRACT: Strata must REFUSE this out loud. See AwkwardFormsTests.fs.
-- `IS NULL` removes a comment: it is an operation, not a state. A project that
-- wants no comment declares none, and removing an undeclared one is the diff's
-- decision. Read as "no comment declared" it would be silently ignored.
COMMENT ON TABLE t IS NULL;
