-- CONTRACT: Strata must REFUSE this, with a reason that says why. Loading it
--           as something narrower is the bug class this corpus exists to catch.
CREATE TABLE t (id bigint PRIMARY KEY);
CREATE FUNCTION touch() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;
CREATE CONSTRAINT TRIGGER ct AFTER INSERT ON t DEFERRABLE FOR EACH ROW EXECUTE FUNCTION touch();
