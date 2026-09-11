# Ground truth — deployment gate matrix

Corpus: `examples/corpus` (11 files). Database: the `sales` fixture.

| # | Proposed change | Expected verdict | Why |
|---|---|---|---|
| 01 | `DROP COLUMN sales.orders.status` | **block** | read by name in several corpus files |
| 02 | `DROP COLUMN sales.orders.total` | **block** | read only via `SELECT *` — the case a text search misses |
| 03 | `ADD COLUMN priority` | **allow** | additive; no existing reader can depend on it |
| 04 | `CREATE TABLE sales.shipment` | **allow** | additive |
| 05 | `TRUNCATE sales.orders` | **block** | unconditional total data loss |
| 06 | `DROP TABLE sales.orders` | **block** | readers, writers and a foreign key depend on it |
| 07 | `ALTER COLUMN status TYPE varchar(10)` | **requires-approval** | affected sources known; type compatibility is a human judgement |
| 08 | `ADD CONSTRAINT chk_total` | **requires-approval** | breaks no reader but may fail against existing rows |
| 09 | `DROP COLUMN note` | **requires-approval** | nothing in the corpus reads it, but the scope does not support an absence claim |

**False-positive check:** 03 and 04 must NOT be blocked. A gate that blocks
additive changes gets disabled (`RK-016`, §136).

**The load-bearing case is 09.** A clean result under an incomplete scope must
not return `allow`. Absence of evidence inside a bounded scope is not evidence
of absence (§144.11, `P-009`).
