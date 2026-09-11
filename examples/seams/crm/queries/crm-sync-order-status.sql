-- Nightly CRM sync.
UPDATE billing.orders SET status = 'void' WHERE account_code IN (
  SELECT account_code FROM crm.account WHERE region = 'defunct'
);
