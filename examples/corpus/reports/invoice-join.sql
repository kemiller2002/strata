-- OBSERVED relationship: no foreign key exists between these columns.
SELECT i.invoice_id, c.name
FROM sales.invoice i
JOIN sales.customer c ON c.customer_number = i.customer_number;
