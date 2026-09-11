-- A range join. Strata does not model this as relationship evidence, and must
-- say so rather than silently finding no relationship.
SELECT o.order_id, c.name
FROM sales.orders o, sales.customer c
WHERE o.customer_id > c.id;
