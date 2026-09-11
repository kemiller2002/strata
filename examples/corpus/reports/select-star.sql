-- A wildcard reader. Its column dependencies come from the catalog, not the text.
SELECT * FROM sales.orders WHERE status = 'open';
