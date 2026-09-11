# Ground truth — messy corpus, "which files read billing.orders.status?"

Six files read it:

| File | Why it counts |
|---|---|
| `billing/queries/status-qualified.sql` | explicit `billing.orders` + `o.status` |
| `billing/queries/status-bare-00.sql` | bare `FROM orders`, resolved by `search_path = billing, public` |
| `billing/queries/status-bare-01.sql` | same |
| `billing/queries/status-bare-02.sql` | same |
| `billing/queries/status-alias.sql` | `FROM billing.orders AS x`, column only ever `x.status` |
| `analytics/queries/orders-dump.sql` | `SELECT *` — reads the column, never names it |

Six traps that do NOT count:

| File | Why it is excluded |
|---|---|
| `analytics/queries/cte-shadow-status.sql` | `WITH orders AS (...)` shadows the table |
| `support/queries/todo-comment.sql` | column named only in a `--` comment |
| `archive/queries/archive-status-00.sql` | `archive.orders`, different table |
| `archive/queries/archive-status-01.sql` | same |
| `support/queries/staging-status.sql` | `staging.orders`, different table |
| `billing/queries/dynamic-status.sql` | target built with `quote_ident()` at runtime; unresolvable |
