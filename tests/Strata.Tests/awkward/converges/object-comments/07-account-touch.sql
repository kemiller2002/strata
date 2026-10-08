CREATE TRIGGER account_touch BEFORE UPDATE ON account FOR EACH ROW EXECUTE FUNCTION touch();
COMMENT ON TRIGGER account_touch ON account IS 'Keeps the row current';
