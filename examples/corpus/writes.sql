UPDATE sales.orders SET status = 'closed' WHERE order_id = 1;
INSERT INTO sales.invoice (customer_number, amount) VALUES ('C-1', 10.00);
