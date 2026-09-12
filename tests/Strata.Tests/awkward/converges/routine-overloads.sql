-- CONTRACT: Strata must read this the way the server does — the diff after
--           introspecting it back must be EMPTY. See AwkwardFormsTests.fs.
-- Identity is name AND argument types. numeric(12,2) contains a comma, which
-- broke a rendered-and-split signature.
CREATE FUNCTION f(a integer) RETURNS integer LANGUAGE sql AS $$ SELECT a $$;
CREATE FUNCTION f(a text) RETURNS text LANGUAGE sql AS $$ SELECT a $$;
CREATE FUNCTION g(a numeric(12,2)) RETURNS numeric LANGUAGE sql AS $$ SELECT a $$;
