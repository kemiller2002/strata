CREATE VIEW active_account AS
    SELECT id, email
    FROM account;
COMMENT ON VIEW active_account IS 'Accounts still open';
COMMENT ON COLUMN active_account.email IS 'A view column has a comment too';
