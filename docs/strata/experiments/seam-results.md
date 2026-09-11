| Task | raw 1 | raw 2 | raw 3 | strata 1 | strata 2 | strata 3 | Winner |
|---|---|---|---|---|---|---|---|
| T1 drop legacy_code — which files break | **ok** | **ok** | **ok** | not determinable | not determinable | not determinable | **raw 3/3, strata 0/3** |
| T2 outside writer to billing.orders | ok | ok | ok | ok | ok | ok | tie 3/3 |
| T3 order_ref target + enforcement | ok | ok | ok | ok | ok | ok | tie 3/3 |
| T4 order_id type change — which modules | ok | ok (most precise) | ok | ok | ok (most careful) | ok | tie 3/3 |
| T5 cross-module unenforced relationships | over-reported 4 | over-reported 4 | over-reported 3 | **exactly 2** | **exactly 2** | **exactly 2** | **strata 3/3, raw 0/3** |
