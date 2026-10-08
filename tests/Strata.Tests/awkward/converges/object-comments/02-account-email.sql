CREATE INDEX account_email ON account (email);
COMMENT ON INDEX account_email IS 'For lookups by email';
