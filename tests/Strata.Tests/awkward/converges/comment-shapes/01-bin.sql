-- CONTRACT: Strata must read this the way the server does — the diff after
--           introspecting it back must be EMPTY, and nothing this case declares
--           may come back not-compared. See AwkwardFormsTests.fs.
--
-- Comments are the fixture. A table's column list was located with a search for
-- the first "(" in the text, so ONE leading comment containing a parenthesis
-- moved the split into the comment and the shadow statement became garbage
-- (WI-0092). Measured live, that took the check constraint, the column default,
-- the policy expression and a view out of the comparison at once — and proposed
-- nothing, so it read as convergence.

-- warehouse bins (aisle, shelf)
CREATE TABLE bin (
    id     bigint PRIMARY KEY,
    label  text NOT NULL DEFAULT 'unlabelled',
    tenant text NOT NULL,
    CONSTRAINT bin_label_not_blank CHECK (length(label) > 0)
);
