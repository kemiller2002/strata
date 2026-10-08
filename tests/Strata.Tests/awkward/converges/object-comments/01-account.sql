-- CONTRACT: Strata must read this the way the server does — the diff after
--           introspecting it back must be EMPTY, and nothing this case declares
--           may come back not-compared. See AwkwardFormsTests.fs.
--
-- COMMENT ON, for every kind of object Strata models a comment on, written in
-- the same file as the object it documents. Before comments were modelled a
-- COMMENT ON was a load failure, so projects kept their documentation as SQL
-- comments instead. The statements share a file with the CREATE on purpose:
-- the CREATE's text is executed verbatim and reshaped by shadow normalisation,
-- and a COMMENT riding along into either would break both.
COMMENT ON SCHEMA awk IS 'Scratch schema for the awkward-forms corpus';

CREATE TABLE account (
    id    bigint CONSTRAINT account_pk PRIMARY KEY,
    email text DEFAULT 'nobody',
    CONSTRAINT email_shape CHECK (email LIKE '%@%' OR email = 'nobody')
);

COMMENT ON TABLE account IS 'One row per customer — accents and all: café';
COMMENT ON COLUMN account.email IS 'It''s optional';
COMMENT ON CONSTRAINT email_shape ON account IS 'A loose check
over two lines';
