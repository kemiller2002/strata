# Ground truth — cross-module seam task set

| T | Correct answer | Which condition should favour it |
|---|---|---|
| T1 | **NOT safe.** Two analytics files read it: `analytics/queries/legacy-recon.sql` and `analytics/queries/legacy-audit.sql`. Both owned by analytics, not billing. | **Favours raw.** Strata has no column-level reader query — `readers billing.invoice` returns all 12 file readers without isolating the column. Raw can grep the literal string. Included deliberately as a test Strata should lose. |
| T2 | Yes — `crm/queries/crm-sync-order-status.sql`, owned by **crm**, issues `UPDATE billing.orders`. | Favours Strata (`writers` is direct); raw must scan 42 files for writes. |
| T3 | It refers to `billing.orders.order_id`. **Not enforced** — there is no foreign key; the link exists only in 3 fulfilment queries. | Favours Strata (observed relationship, enforcedByDatabase=false). |
| T4 | **billing** (FK `billing.invoice.order_id` references it), **fulfilment** (`shipment.order_ref` joins to it in 3 files), **analytics** (`order-dump.sql` selects from billing.orders; `v_order_revenue` exposes order_id). crm is *not* affected via order_id — crm links on account_code. | Favours Strata (relationships + readers); raw must trace manually. |
| T5 | Two: `billing.orders.account_code` ↔ `crm.account.account_code`; `billing.orders.order_id` ↔ `fulfilment.shipment.order_ref`. Both observed-only, neither has a foreign key. | Favours Strata (relationship kinds are explicit). |

Scoring: a task is correct only if the answer names the right artefacts AND
gets the enforcement/safety judgement right. Partial credit noted separately.
