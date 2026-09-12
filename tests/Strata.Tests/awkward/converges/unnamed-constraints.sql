-- CONTRACT: Strata must read this the way the server does — the diff after
--           introspecting it back must be EMPTY. See AwkwardFormsTests.fs.
-- The server names each of these; the file does not. A fabricated name made
-- every such project churn forever.
CREATE TABLE parent (id bigint PRIMARY KEY);
CREATE TABLE t (
    id        bigint PRIMARY KEY,
    code      text   NOT NULL UNIQUE,
    parent_id bigint REFERENCES parent (id),
    total     numeric(12,2) NOT NULL CHECK (total >= 0)
);
