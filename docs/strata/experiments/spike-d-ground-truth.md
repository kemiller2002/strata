# Ground truth for the Spike D correctness task set

| Q | Correct answer | Notes |
|---|---|---|
| Q1 | `orders.customer_id = customer.id` | Declared foreign key. Present in raw DDL and in Strata. |
| Q2 | `invoice.customer_number = customer.customer_number`, **NOT enforced** by the database (no foreign key) | **Discriminator.** Raw DDL shows no FK, so the relationship is only discoverable by reading the corpus SQL and inferring. Strata states it with `kind: observed` and `enforcedByDatabase: false`. A wrong answer is either "no relationship" or claiming it IS enforced. |
| Q3 | `'open'` and `'closed'` | From the CHECK constraint. This is the fact `inspect` omitted until EV-STRATA-2026-A2B8. |
| Q4 | Yes — `idx_orders_customer` | Also previously omitted from Strata. |
| Q5 | `UPDATE sales.orders SET status='closed' WHERE order_id = <value>` | Must bound on the primary key `order_id`. Bounding on `customer_id` or `status` can affect many rows and is wrong. |
