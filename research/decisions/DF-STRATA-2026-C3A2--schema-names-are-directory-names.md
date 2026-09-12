---
id: DF-STRATA-2026-C3A2
title: The managed schema set is derived from directory names, which must equal the schema name exactly and match a portable identifier pattern
status: accepted
decision_type: product-architecture
created: 2026-09-12
updated: 2026-09-12
created_by_agent: claude
confidence: medium
supersedes: [DF-STRATA-2026-A4D9]
superseded_by: []
evidence: []
tags: [strata, schemas, identifiers, portability, ng-006]
---

# Decision Record

## Decision

The set of schemas Strata manages is **derived from the directories under
`schemaRoot`**, not declared in `strata.json`. The `managedSchemas` field is
removed.

A directory name must equal `pg_namespace.nspname` **byte for byte**, and a
schema is manageable only if its name matches:

```
^[a-z_][a-z0-9_]*$
```

and is not a Windows reserved device name (`aux`, `con`, `prn`, `nul`,
`com1`–`com9`, `lpt1`–`lpt9`).

A schema in the database with no corresponding directory is **ignored when
comparing** and **reported** as something Strata knows nothing about.

## Rationale

### Deriving from the tree is safer than declaring

`managedSchemas` and the set of schemas actually declared were two concepts, and
the gap between them was the dangerous case the code already comments on: a
schema listed as managed with **no declared objects** reads as *this schema
should be empty*, which is authority to drop everything in it.

Deriving from directories inverts the failure mode. Delete a schema's files and
the schema becomes **unmanaged** — Strata stops touching it — rather than
**empty** — Strata empties it. Fail-safe instead of fail-dangerous.

This collapse is only safe because of the next point.

### An empty schema directory is a compile error

Otherwise the hazard returns in a new shape. Under a required `include`
(`DF-STRATA-2026-7D14`) a schema directory with no included files contributes
nothing, so it can only be an accident or leftovers from deleting files. Failing
on it closes the gap that `managedSchemas` existed to hedge against.

### Exact match removes the need for a convention

PostgreSQL folds unquoted identifiers to lowercase, so the *catalog* name is the
only unambiguous one. The parser already applies that folding when reading a
declaration, so the check is one comparison with no interpretation:

- `schema/orders/` + `CREATE TABLE orders.foo` → folds to `orders`, matches.
- `schema/Orders/` + `CREATE TABLE Orders.foo` → folds to `orders`, directory
  says `Orders` → **error**. The file must write `"Orders".foo` to mean that
  schema.

### Restriction rather than encoding

PostgreSQL's identifier space is not a subset of any filesystem's name space,
and filesystems disagree with each other. One pattern eliminates four hazards at
once:

| hazard | why the pattern kills it |
|---|---|
| case sensitivity | no uppercase, so `orders`/`Orders` cannot collide on macOS or Windows |
| Unicode normalisation | ASCII only, so NFC/NFD cannot differ between Linux and macOS |
| illegal path characters | `/`, `\`, `:` and control characters excluded |
| Windows device names | excluded by an explicit list on top of the pattern |

It is also exactly the set of identifiers needing no quoting in SQL, so the file
content and the directory name are the same string with no folding to reason
about.

Any escaping scheme for arbitrary identifiers would reintroduce the ambiguity
this record exists to remove, in exchange for supporting schemas that are
vanishingly rare and generally regretted.

## Consequences

- **A non-matching schema is unmanaged, not broken.** It is reported and never
  touched — the state already defined for anything outside the project.
- **It must fail closed on the declaration side.** A file declaring an object in
  such a schema (`CREATE TABLE "Orders".foo`) is a **compile error**; otherwise
  the project asserts something it cannot represent. The message must say why, or
  it reads as arbitrary:

  ```
  error: schema "Orders" cannot be managed by Strata.
         Schema names must match [a-z_][a-z0-9_]* so that a directory name can
         equal the schema name on every filesystem. Uppercase, non-ASCII and
         path-reserved characters are excluded because Linux, macOS and Windows
         disagree about them.
  ```

- **A schema whose name cannot be a path segment needs no new handling.** It
  appears in the database with no directory, which is the ignored-and-reported
  case above. It falls out of the rule rather than needing one.
- **Byte comparison fails closed on case-insensitive filesystems.** `schema/orders/`
  opens successfully on macOS when the directory is named `Orders`, but the
  listing returns `Orders`, the comparison fails, and compile errors. Correct
  outcome, no extra mechanism.
- `SchemaDiff.Inputs` currently carries both `ManagedSchemas` and
  `DeclaredInSchemas`; they become one field.

## Limitations

A project cannot manage two schemas differing only by case, because no
case-insensitive filesystem can hold both directories. That is the filesystem's
limitation rather than Strata's, and the pattern forbids the second name anyway.
