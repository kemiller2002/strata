-- CONTRACT: Strata must read this the way the server does — the diff after
--           introspecting it back must be EMPTY. See AwkwardFormsTests.fs.
-- Case and punctuation only survive because they are quoted. Folding either
-- side would make these compare unequal to themselves.
CREATE TABLE "MixedCase" (
    "Id"          bigint PRIMARY KEY,
    "select"      text   NOT NULL,
    "with space"  int
);
