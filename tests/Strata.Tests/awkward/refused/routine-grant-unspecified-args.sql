-- CONTRACT: Strata must REFUSE this, with a reason that says why. Loading it
--           as something narrower is the bug class this corpus exists to catch.
-- No argument list at all, which parses as `args_unspecified` rather than as
-- `f()`. PostgreSQL resolves it against whatever is deployed and fails if more
-- than one `f` exists, so the FILE does not say which overload it means.
--
-- Read without that flag it is indistinguishable from a zero-argument routine,
-- and the grant lands on a function the file never named.
CREATE FUNCTION f(a integer) RETURNS integer LANGUAGE sql AS $$ SELECT a $$;
GRANT EXECUTE ON FUNCTION f TO strata_awkward_role;
