-- A column list before the keyword. `(shipment_id, shipment_weight)` sits at
-- parenthesis depth one and is not where the body starts.
CREATE VIEW shipment_summary (shipment_id, shipment_weight) AS
    SELECT id, weight
    FROM shipment;
