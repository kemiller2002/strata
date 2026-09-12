-- CONTRACT: Strata must read this the way the server does — the diff after
--           introspecting it back must be EMPTY, and nothing this case declares
--           may come back not-compared. See AwkwardFormsTests.fs.
CREATE TABLE parent (id bigint PRIMARY KEY);
