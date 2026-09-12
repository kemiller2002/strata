-- CONTRACT: Strata must read this the way the server does — the diff after
--           introspecting it back must be EMPTY. See AwkwardFormsTests.fs.
-- CREATE SEQUENCE carries no options, so every default is the tool's
-- responsibility. Descending inverts min, max AND start.
CREATE SEQUENCE bare;
CREATE SEQUENCE typed AS integer;
CREATE SEQUENCE descending INCREMENT BY -1;
CREATE SEQUENCE explicit AS bigint START WITH 5 INCREMENT BY 2
    MINVALUE 1 MAXVALUE 100 CACHE 3 CYCLE;
