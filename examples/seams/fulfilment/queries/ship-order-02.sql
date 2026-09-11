-- Joins fulfilment to billing. There is no foreign key.
SELECT s.shipment_id, o.status
FROM fulfilment.shipment s
JOIN billing.orders o ON o.order_id = s.order_ref
WHERE o.status = 'open';
