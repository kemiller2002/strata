-- The server names each of these; the file does not. A fabricated name made
-- every such project churn forever.
--
-- Split from parent so the declaring text is recorded: an unnamed check can
-- only be attributed to a deployed one through the server's rendering of the
-- declared DDL, and that rendering needs the file's verbatim text.
CREATE TABLE t (
    id        bigint PRIMARY KEY,
    code      text   NOT NULL UNIQUE,
    parent_id bigint REFERENCES parent (id),
    total     numeric(12,2) NOT NULL CHECK (total >= 0)
);
