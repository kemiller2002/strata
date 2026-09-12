-- CONTRACT: Strata must read this the way the server does — the diff after
--           introspecting it back must be EMPTY. See AwkwardFormsTests.fs.
-- A unique index and a constraint-backed one are different objects; only one
-- of them is the project's to manage.
CREATE TABLE t (id bigint PRIMARY KEY, a int, b int, status text);
CREATE INDEX plain ON t (a);
CREATE UNIQUE INDEX uniq ON t (b);
CREATE INDEX composite ON t (a, b);
