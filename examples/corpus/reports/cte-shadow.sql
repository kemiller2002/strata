-- A CTE named after a real table. Must NOT create a dependency on sales.orders.
WITH orders AS (SELECT 1 AS id, 'x'::text AS status)
SELECT id FROM orders WHERE status = 'x';
