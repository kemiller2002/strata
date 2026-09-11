# Ground truth — scale seam task set (3,009 files, 200 tables)

| S | Correct answer |
|---|---|
| S1 | THREE files break: `audit/queries/settlement-recon.sql` and `audit/queries/settlement-trace.sql` (both **audit**, explicit reads), **plus** `analytics/queries/billing-dump.sql` (**analytics**) which is `SELECT * FROM billing.t_07` — a genuine reader containing **no occurrence of the column name**. A grep for the column finds only the first two. This is the discriminator. |
| S2 | Yes — `support/queries/support-adjust-billing.sql` (**support**): `UPDATE billing.t_02 SET amount = 0 WHERE code = 'WRITEOFF'`. |
| S3 | Refers to `billing.t_00.id`. **Not enforced** — no foreign key; the link exists only in 3 fulfilment queries. |
| S4 | Two: `fulfilment.t_03.billing_order_ref` → `billing.t_00.id`; `partner.t_11.crm_account_code` → `crm.t_05.account_code`. All 199 declared FKs are intra-module. |
