-- CONTRACT: Strata must read this the way the server does — the diff after
--           introspecting it back must be EMPTY, and nothing this case declares
--           may come back not-compared. See AwkwardFormsTests.fs.
--
-- Partial indexes. Their WHERE predicates used to be compared by PRESENCE
-- only, so `WHERE status = 'open'` and `WHERE status = 'closed'` read as the
-- same index and the difference was disclosed rather than seen. Each predicate
-- here is one the server rewrites — an implicit cast, an IN list that becomes
-- `= ANY (ARRAY[...])`, a conjunction it parenthesises — so the declared text
-- and the catalog's never match as written and must be rendered to compare.
CREATE TABLE ticket (
    id        bigint PRIMARY KEY,
    status    text NOT NULL,
    priority  integer,
    closed_at timestamptz
);
