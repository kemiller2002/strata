-- The enum the domain in 04 stands on. One object per file: the declaring text
-- is only attributed to an object when its file declares exactly one, and a
-- domain whose text is not attributed is never rendered and never compared.
CREATE TYPE shipment_state AS ENUM ('queued', 'sent', 'lost');
