-- `total` is read by select-star.sql via SELECT *, and by nothing by name.
ALTER TABLE sales.orders DROP COLUMN total;
