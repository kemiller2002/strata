CREATE TABLE t (id bigint PRIMARY KEY, at timestamptz);
INSERT INTO t (id, at) VALUES (1, now());
