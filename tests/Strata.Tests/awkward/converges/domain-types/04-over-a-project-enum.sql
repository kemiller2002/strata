-- A domain over a type the SAME project declares. The shadow that renders this
-- declaration has to have that enum available or the CREATE DOMAIN fails and the
-- domain is disclosed as not-compared — honest, and a comparison that should
-- have happened.
CREATE DOMAIN live_shipment AS shipment_state
    NOT NULL
    CHECK (VALUE <> 'lost');
