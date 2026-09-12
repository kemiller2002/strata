-- CONTRACT: Strata must REFUSE this, with a reason that says why. Loading it
--           as something narrower is the bug class this corpus exists to catch.
--
-- One ALTER TABLE carrying a row-security change AND something else. Reading
-- only the part Strata models would enable row-level security and silently drop
-- the column the same statement asked for.
CREATE TABLE t (id bigint PRIMARY KEY, note text);
ALTER TABLE t ENABLE ROW LEVEL SECURITY, ADD COLUMN extra text;
