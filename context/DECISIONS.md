# Strata decisions

Material decisions use `DF-` records under `research/decisions/`. This compact
table is a navigation view, not a replacement for those records.

| Date | Decision | Status | Rationale | Record |
|---|---|---|---|---|
| 2026-09-11 | Use ROS 2.0.1 as a measured greenfield pilot. | provisional | Test portability and operational value on a real beginning project. | Not yet promoted to a `DF-` record |
| 2026-09-11 | Pin `ros.json` `rosVersion` to `2.0.1` instead of the installed `2.0.1-main.78.1`. | provisional | The `@main` npm build has no matching GitHub release, so the `./ros` launcher 404s on `v2.0.1-main.78.1/checksums.txt`; `v2.0.1` is the newest tag publishing verified binaries. | Not yet promoted to a `DF-` record |
