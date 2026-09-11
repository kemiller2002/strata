SELECT i.invoice_id, i.amount, o.status
FROM billing.invoice i
JOIN billing.orders o ON o.order_id = i.order_id
WHERE o.status = 'paid' AND i.amount > 20;
