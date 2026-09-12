-- Case and punctuation only survive because they are quoted. Folding either
-- side would make these compare unequal to themselves.
CREATE TABLE "MixedCase" (
    "Id"          bigint PRIMARY KEY,
    "select"      text   NOT NULL,
    "with space"  int
);
