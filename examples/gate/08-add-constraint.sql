ALTER TABLE sales.orders ADD CONSTRAINT chk_total CHECK (total >= 0);
