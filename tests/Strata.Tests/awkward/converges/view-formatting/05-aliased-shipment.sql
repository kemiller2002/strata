-- Column aliases spelled with the same keyword. Only the FIRST `AS` at depth
-- zero opens the body; the rest belong to the query.
CREATE VIEW aliased_shipment AS
    SELECT id AS shipment_id, weight AS shipment_weight
    FROM shipment;
