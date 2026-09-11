DROP SCHEMA IF EXISTS billing CASCADE;
DROP SCHEMA IF EXISTS archive CASCADE;
DROP SCHEMA IF EXISTS staging CASCADE;
DROP SCHEMA IF EXISTS analytics CASCADE;
DROP SCHEMA IF EXISTS support CASCADE;

CREATE SCHEMA billing;
CREATE SCHEMA archive;
CREATE SCHEMA staging;
CREATE SCHEMA analytics;
CREATE SCHEMA support;

-- The SAME table name in three schemas. A bare `orders` is ambiguous to a
-- reader and to grep; only search_path resolves it.
CREATE TABLE billing.orders (
  order_id bigserial PRIMARY KEY,
  customer_id bigint NOT NULL,
  status text NOT NULL,
  total numeric(12,2) NOT NULL DEFAULT 0,
  note text
);
CREATE TABLE archive.orders (
  order_id bigint PRIMARY KEY,
  status text,
  archived_on date
);
CREATE TABLE staging.orders (
  order_id bigint,
  status text,
  loaded_at timestamptz
);

CREATE TABLE billing.customer (
  id bigserial PRIMARY KEY,
  customer_ref text NOT NULL UNIQUE,
  name text NOT NULL
);
CREATE TABLE analytics.summary (
  id bigserial PRIMARY KEY,
  bucket text,
  amount numeric(12,2)
);
CREATE TABLE support.ticket (
  ticket_id bigserial PRIMARY KEY,
  order_ref bigint,
  body text
);

GRANT USAGE ON SCHEMA billing, archive, staging, analytics, support TO strata;
GRANT SELECT ON ALL TABLES IN SCHEMA billing, archive, staging, analytics, support TO strata;
