CREATE TABLE t (id bigint PRIMARY KEY, code text);
INSERT INTO t (id, code) VALUES (1, 'a') ON CONFLICT (id) DO NOTHING;
