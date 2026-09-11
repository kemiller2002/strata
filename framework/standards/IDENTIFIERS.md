# Identifier Standard

**Status:** Canonical  
**Version:** 1.0.0

## Format

New records use:

```text
<PREFIX>-<AREA>-<YYYY>-<TOKEN>
```

`PREFIX` is one of `RP`, `JR`, `EV`, `HY`, `TH`, `EX`, `DF`, `CN`, `GL`, or
`MS`. `AREA` is uppercase letters, digits, or hyphens. `YYYY` is the creation
year. `TOKEN` is either four decimal digits for an already coordinated legacy
sequence or four uppercase hexadecimal characters for parallel-safe creation.

Example: `EV-ROS-2026-A7F2`.

## Allocation

Sequential allocation is unsafe across parallel Git branches without a lock.
New work should generate a random hexadecimal token and retry on collision.
The validator, not allocation order, is the final uniqueness authority. Gaps
are valid. IDs are stable, case-sensitive, and never reused.

The filename for a canonical Markdown artifact is:

```text
<ID>--<short-kebab-title>.md
```

Templates may use placeholders. Real artifacts must use a filename beginning
with their ID.
