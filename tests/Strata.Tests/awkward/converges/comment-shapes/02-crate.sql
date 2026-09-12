/* A block comment with a parenthesis ( and a nested /* one ( too */ before the
   statement it describes. Block comments NEST in PostgreSQL, so a scanner that
   stops at the first closer leaves the rest of this text looking like SQL. */
CREATE TABLE crate (
    id      bigint PRIMARY KEY,
    bin_id  bigint NOT NULL REFERENCES bin (id),
    volume  numeric(8,3) NOT NULL DEFAULT 0.0
);
