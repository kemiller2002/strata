-- An IN (SELECT ...) correlation. Also not modelled as relationship evidence.
SELECT c.name
FROM sales.customer c
WHERE c.id IN (SELECT o.customer_id FROM sales.orders o WHERE o.status = 'open');
