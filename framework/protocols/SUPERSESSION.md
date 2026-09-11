# Supersession and Amendment Protocol

**Status:** Canonical  
**Version:** 1.0.0

## Rules

1. IDs are never reused.
2. Substantive changes to an accepted artifact create a new artifact.
3. The replacement lists the prior ID in `supersedes`.
4. The prior artifact lists the replacement ID in `superseded_by`.
5. Supersession links must be reciprocal, may not target the same record, and
   may not form a cycle.
6. Superseded and withdrawn records remain in their canonical directory and
   generated registries.

Metadata-only corrections to accepted records are limited to broken links,
typographical metadata, and administrative ownership. Record them under an
`amendments` list with date, author, reason, and fields changed. A correction
that changes a claim, interpretation, confidence, or disposition is
substantive.

Draft records may be replaced without supersession only before any other
canonical artifact references them. Deleting referenced records is prohibited.
