-- CONTRACT: Strata must read this the way the server does — the diff after
--           introspecting it back must be EMPTY, and nothing this case declares
--           may come back not-compared. See AwkwardFormsTests.fs.
--
-- Everything a domain can carry, in one declaration: a base type, a DEFAULT,
-- NOT NULL, a NAMED check and an UNNAMED one. None of the expressions survives
-- the parse tree, so every one of them is the server's own rendering on both
-- sides or this case reports a difference on a domain nobody touched.
--
-- The unnamed CHECK is the WI-0054 shape: PostgreSQL calls it `email_check`,
-- which no file can predict, so it is matched by its predicate and never by a
-- fabricated name.
CREATE DOMAIN email AS text
    DEFAULT 'nobody@example.com'
    NOT NULL
    CONSTRAINT email_shape CHECK (VALUE ~ '@')
    CHECK (length(VALUE) < 100);
