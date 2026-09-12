-- CONTRACT: Strata must read this the way the server does — the diff after
--           introspecting it back must be EMPTY, and nothing this case declares
--           may come back not-compared. See AwkwardFormsTests.fs.
-- A directory case, one object per file, because normalisation needs a file's
-- verbatim text and that is only recorded for a file declaring ONE object.
CREATE TABLE shipment (
    id      bigint PRIMARY KEY,
    status  text NOT NULL DEFAULT 'pending',
    weight  numeric(10,2) NOT NULL,
    CONSTRAINT shipment_weight_positive CHECK (weight > 0)
);
