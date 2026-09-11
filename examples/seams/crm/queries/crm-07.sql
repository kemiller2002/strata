SELECT a.display_name, c.email
FROM crm.account a
JOIN crm.contact c ON c.account_id = a.account_id
WHERE a.region = 'r7';
