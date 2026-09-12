-- An unnamed index gets a server-generated name a file cannot predict.
CREATE TABLE t (id bigint PRIMARY KEY, a int);
CREATE INDEX ON t (a);
