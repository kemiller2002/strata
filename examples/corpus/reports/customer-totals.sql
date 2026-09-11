SELECT c.name, sum(o.total) AS total
FROM sales.customer c
JOIN sales.orders o ON o.customer_id = c.id
GROUP BY c.name;
