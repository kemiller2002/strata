-- The shape that hid WI-0092. `AS` at the end of a line is how views are
-- ordinarily written, and it was enough to drop this view out of normalisation
-- entirely: the body was located with a search for the literal " AS ", which
-- wants a space on both sides. The view was then never compared, and the corpus
-- could not tell, because a comparison that does not happen produces the same
-- empty change list as one that succeeds.
CREATE VIEW pending_shipment AS
    SELECT id, weight
    FROM shipment
    WHERE status = 'pending';
