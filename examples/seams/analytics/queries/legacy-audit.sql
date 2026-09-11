SELECT i.invoice_id, i.legacy_code
FROM billing.invoice i
JOIN billing.orders o ON o.order_id = i.order_id
WHERE i.legacy_code LIKE 'L-%';
