-- A routine grant is keyed on the ARGUMENT TYPES, so the two overloads below
-- are two different grants and one of them is granted to nobody. Getting the
-- key wrong matches the declared grant against the wrong function, silently.
--
-- The argument types must also be spelled the way the routine's own signature
-- is spelled: `numeric(12,2)` here and `numeric` in the catalog would never
-- match, and `int` must fold to `integer`.
--
-- PUBLIC already holds EXECUTE on every function by default — proacl is NULL,
-- which is not "nobody holds anything" — so a grant to PUBLIC must compare
-- equal to that default rather than be proposed again on every run.
CREATE FUNCTION f(a integer) RETURNS integer LANGUAGE sql AS $$ SELECT a $$;
CREATE FUNCTION f(a text) RETURNS text LANGUAGE sql AS $$ SELECT a $$;
CREATE FUNCTION g(a numeric(12,2)) RETURNS numeric LANGUAGE sql AS $$ SELECT a $$;
CREATE PROCEDURE p() LANGUAGE sql AS $$ SELECT 1 $$;

GRANT EXECUTE ON FUNCTION f(int) TO strata_awkward_role;
GRANT EXECUTE ON FUNCTION g(numeric(12,2)) TO strata_awkward_role2;
GRANT EXECUTE ON PROCEDURE p() TO strata_awkward_role;
GRANT EXECUTE ON ROUTINE f(text) TO PUBLIC;
