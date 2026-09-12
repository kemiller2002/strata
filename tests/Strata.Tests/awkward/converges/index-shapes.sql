-- A unique index and a constraint-backed one are different objects; only one
-- of them is the project's to manage.
CREATE TABLE t (id bigint PRIMARY KEY, a int, b int, status text);
CREATE INDEX plain ON t (a);
CREATE UNIQUE INDEX uniq ON t (b);
CREATE INDEX composite ON t (a, b);
