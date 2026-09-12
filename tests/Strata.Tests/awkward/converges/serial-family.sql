-- Not types. Each becomes an integer type plus a nextval default, and the
-- sequence is the column's, not the project's.
CREATE TABLE t (
    a smallserial,
    b serial,
    c bigserial,
    d serial4,
    e serial8
);
