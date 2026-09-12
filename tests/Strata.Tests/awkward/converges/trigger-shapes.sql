-- AFTER is the absence of a bit, not a bit. UPDATE OF narrows the events.
-- Event order is a bitmask on both sides, so it cannot follow the author's.
CREATE TABLE t (id bigint PRIMARY KEY, total numeric, status text);
CREATE FUNCTION touch() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;
CREATE TRIGGER before_row BEFORE INSERT ON t FOR EACH ROW EXECUTE FUNCTION touch();
CREATE TRIGGER after_stmt AFTER UPDATE OR DELETE OR INSERT ON t FOR EACH STATEMENT EXECUTE FUNCTION touch();
CREATE TRIGGER update_of AFTER UPDATE OF total, status ON t FOR EACH ROW EXECUTE FUNCTION touch();
CREATE TRIGGER truncate_only AFTER TRUNCATE ON t FOR EACH STATEMENT EXECUTE FUNCTION touch();
