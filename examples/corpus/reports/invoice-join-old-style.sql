-- Same relationship written as an implicit join in the older style.
SELECT i.amount, c.name
FROM sales.invoice i, sales.customer c
WHERE c.customer_number = i.customer_number;
