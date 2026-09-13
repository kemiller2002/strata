-- A column declared WITH the type, so the round trip covers reading the column
-- back as that type rather than as its underlying representation.
CREATE TABLE shipment (
    id     bigint PRIMARY KEY,
    status order_status NOT NULL DEFAULT 'pending'
);
