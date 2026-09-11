SELECT s.shipment_id, s.carrier, r.rate
FROM fulfilment.shipment s
JOIN fulfilment.carrier_rate r ON r.carrier = s.carrier
WHERE s.shipped_on > date '2026-05-01';
