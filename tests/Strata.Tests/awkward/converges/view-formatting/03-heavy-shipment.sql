-- The shape that always worked: `AS` with a space on either side, on one line.
CREATE VIEW heavy_shipment AS SELECT id FROM shipment WHERE weight > 100;
