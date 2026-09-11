# Strata research queue

The notebook's `Q-001`–`Q-030` (§131) are preserved in full in
`docs/strata/REQUIREMENTS-ANALYSIS.md` section G. This queue holds the questions
that **block current or next-phase work**, in priority order.

| Priority | Question | Decision affected | Discriminating evidence | Status |
|---:|---|---|---|---|
| 1 | `Q-005` How much semantic binding can be done offline? | Whether Strata needs binder logic or a live server | Spike B remainder: live-server comparison of `search_path`, overloads, casts, operators | **partially answered** — `EV-STRATA-2026-B9C4` covers the offline half |
| 2 | `Q-027` Can `PREPARE`/`EXPLAIN` avoid implementing binder logic? | Size of the resolution component | Live-server trial against the same cases | open (`HY-STRATA-2026-5E03`) |
| 3 | `Q-002` Minimum supported PostgreSQL majors? | Parser pinning, compatibility reporting | Parser reports 17.5; decide supported range and divergence behaviour | open |
| 4 | `Q-026` What if live DB major differs from parser target? | Analysis validity | Behaviour matrix across majors | open |
| 5 | `Q-007` What is the first policy profile? | S4 validation scope | Candidate profile against real corpus false-positive rate | open (blocks P2) |
| 6 | `Q-021` When should policy warn versus block on unknown relationships? | Guardrail social acceptance | `§136` bypass risk; measured false-positive rate | open (blocks P2) |
| 7 | `Q-006` When should Strata use a live server to resolve or validate? | Offline/online split | Spike B remainder | open |
| 8 | `Q-003` Desired schema authoring source? | P4 schema diff | Candidate comparison | open (blocks P4) |
| 9 | `Q-004` How are stable managed-object IDs represented? | Rename safety, identity | `§7` identity-versus-name analysis | open (blocks P4) |
| 10 | `Q-010` / `Q-011` / `Q-020` Snapshot storage, embedded persistence, commit-to-Git? | Persistence boundary | Determinism and reviewability requirements | open (blocks P4) |
| 11 | `Q-013` How do we identify external database consumers? | Impact-claim soundness (`RK-005`) | Scope-declaration mechanism | open, non-blocking |
| 12 | `Q-022` How do we calibrate relationship confidence? | Whether categorical certainty stays categorical | `§144` warns against uncalibrated decimal scores | open, non-blocking |
| 13 | `Q-030` Minimal deployment feature demonstrating value without unacceptable risk? | P5 scope | Risk/value comparison | open (blocks P5) |

Remaining open questions (`Q-008`, `Q-009`, `Q-012`, `Q-014`–`Q-019`, `Q-023`–`Q-025`,
`Q-028`, `Q-029`) are recorded in the requirements analysis and are not blocking
any currently planned work.

**Resolved:** `Q-001` (parser wrapper) → `DF-STRATA-2026-4C7A`.
