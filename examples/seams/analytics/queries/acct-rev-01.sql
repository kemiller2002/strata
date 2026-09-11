SELECT a.display_name, sum(o.total)
FROM billing.orders o
JOIN crm.account a ON a.account_code = o.account_code
GROUP BY a.display_name;
