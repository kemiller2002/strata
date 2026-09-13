-- CONTRACT: Strata must REFUSE this out loud. See AwkwardFormsTests.fs.
-- A composite type is not modelled. It used to fall through to the catch-all
-- and produce no declaration at all, so a file declaring one compiled clean and
-- the type was simply absent from desired state — silently unmodelled, which is
-- the failure the whole declaration vocabulary exists to prevent (ER-008).
CREATE TYPE address AS (street text, city text, postcode text);
