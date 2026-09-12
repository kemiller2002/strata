-- CONTRACT: Strata must REFUSE this, with a reason that says why. Loading it
--           as something narrower is the bug class this corpus exists to catch.
--
-- ALTER EXTENSION ... UPDATE TO describes an OPERATION, not a state. The
-- declarative form is CREATE EXTENSION ... VERSION; letting both in would have
-- two files disagree about the same fact.
ALTER EXTENSION citext UPDATE TO '1.7';
