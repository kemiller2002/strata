-- Declared relationship: orders.customer_id -> customer.id (also a real FK).
SELECT o.order_id, c.name, o.total
FROM sales.orders o
JOIN sales.customer c ON c.id = o.customer_id
WHERE o.status = 'open';
