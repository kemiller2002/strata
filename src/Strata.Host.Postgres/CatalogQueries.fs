namespace Strata.Host.Postgres

/// The catalog SQL Strata issues.
///
/// Authority for: exactly what Strata reads from a target database.
///
/// Kept as literal SQL in one module so it is auditable: a reviewer can see
/// every statement Strata runs against a customer database without reading
/// F# (ER-020, "Strata should make its decisions inspectable"). Every query
/// here is a SELECT. There is no code path in this project that writes.
///
/// System schemas are excluded so that extension- and system-owned objects do
/// not arrive looking like ordinary user objects (RK-008, RK-009). Objects
/// belonging to an extension are excluded via pg_depend rather than by
/// name-guessing.
module CatalogQueries =

    /// Schemas visible to the connected role, excluding system schemas.
    let schemas =
        """
        SELECT n.nspname
        FROM pg_catalog.pg_namespace n
        WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
          AND n.nspname NOT LIKE 'pg_toast%'
          AND n.nspname NOT LIKE 'pg_temp%'
        ORDER BY n.nspname
        """

    /// Tables and views, with extension ownership and — critically — whether the
    /// connected role can actually READ each one.
    ///
    /// PostgreSQL's catalog is world-readable: revoking `USAGE` on a schema
    /// hides that schema's DATA, not its METADATA. A role with no access still
    /// sees the objects in `pg_class`. So catalog visibility does NOT imply
    /// accessibility, and Strata must report the two separately or it will
    /// claim to have analysed objects it cannot read (see RK-004, corrected).
    let relations =
        """
        SELECT n.nspname          AS schema_name,
               c.relname          AS relation_name,
               c.relkind::text    AS relkind,
               (d.objid IS NOT NULL) AS extension_owned,
               pg_catalog.has_schema_privilege(n.nspname, 'USAGE') AS schema_accessible,
               pg_catalog.has_table_privilege(c.oid, 'SELECT')     AS relation_readable
        FROM pg_catalog.pg_class c
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        LEFT JOIN pg_catalog.pg_depend d
               ON d.objid = c.oid
              AND d.classid = 'pg_class'::regclass
              AND d.deptype = 'e'
        WHERE c.relkind IN ('r', 'v', 'm', 'p')
          AND n.nspname NOT IN ('pg_catalog', 'information_schema')
          AND n.nspname NOT LIKE 'pg_toast%'
          AND n.nspname NOT LIKE 'pg_temp%'
        ORDER BY n.nspname, c.relname
        """

    /// Columns, in ordinal order.
    let columns =
        """
        SELECT n.nspname                                    AS schema_name,
               c.relname                                    AS relation_name,
               a.attname                                    AS column_name,
               pg_catalog.format_type(a.atttypid, a.atttypmod) AS type_name,
               NOT a.attnotnull                             AS is_nullable,
               a.attnum                                     AS ordinal,
               (a.atthasdef AND ad.adbin IS NOT NULL)       AS has_default,
               (a.attgenerated <> '')                       AS is_generated,
               (a.attidentity <> '')                        AS is_identity
        FROM pg_catalog.pg_attribute a
        JOIN pg_catalog.pg_class c ON c.oid = a.attrelid
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        LEFT JOIN pg_catalog.pg_attrdef ad ON ad.adrelid = c.oid AND ad.adnum = a.attnum
        WHERE a.attnum > 0
          AND NOT a.attisdropped
          AND c.relkind IN ('r', 'v', 'm', 'p')
          AND n.nspname NOT IN ('pg_catalog', 'information_schema')
          AND n.nspname NOT LIKE 'pg_toast%'
          AND n.nspname NOT LIKE 'pg_temp%'
        ORDER BY n.nspname, c.relname, a.attnum
        """

    /// Constraints. `conkey`/`confkey` are expanded to column names in SQL so
    /// the adapter does not reimplement attribute-number resolution.
    let constraints =
        """
        SELECT n.nspname   AS schema_name,
               c.relname   AS relation_name,
               con.conname AS constraint_name,
               con.contype::text AS constraint_type,
               pg_catalog.pg_get_constraintdef(con.oid) AS definition,
               COALESCE(
                 (SELECT array_agg(att.attname ORDER BY k.ord)
                  FROM unnest(con.conkey) WITH ORDINALITY AS k(attnum, ord)
                  JOIN pg_catalog.pg_attribute att
                    ON att.attrelid = con.conrelid AND att.attnum = k.attnum),
                 '{}') AS column_names,
               fn.nspname AS referenced_schema,
               fc.relname AS referenced_relation,
               COALESCE(
                 (SELECT array_agg(att.attname ORDER BY k.ord)
                  FROM unnest(con.confkey) WITH ORDINALITY AS k(attnum, ord)
                  JOIN pg_catalog.pg_attribute att
                    ON att.attrelid = con.confrelid AND att.attnum = k.attnum),
                 '{}') AS referenced_columns
        FROM pg_catalog.pg_constraint con
        JOIN pg_catalog.pg_class c ON c.oid = con.conrelid
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        LEFT JOIN pg_catalog.pg_class fc ON fc.oid = con.confrelid
        LEFT JOIN pg_catalog.pg_namespace fn ON fn.oid = fc.relnamespace
        WHERE con.contype IN ('p', 'u', 'c', 'f')
          AND n.nspname NOT IN ('pg_catalog', 'information_schema')
        ORDER BY n.nspname, c.relname, con.conname
        """

    let indexes =
        """
        SELECT n.nspname    AS schema_name,
               c.relname    AS relation_name,
               ic.relname   AS index_name,
               i.indisunique AS is_unique,
               pg_catalog.pg_get_expr(i.indpred, i.indrelid) AS predicate,
               COALESCE(
                 (SELECT array_agg(att.attname ORDER BY k.ord)
                  FROM unnest(i.indkey::int[]) WITH ORDINALITY AS k(attnum, ord)
                  JOIN pg_catalog.pg_attribute att
                    ON att.attrelid = i.indrelid AND att.attnum = k.attnum
                  WHERE k.attnum > 0),
                 '{}') AS column_names
        FROM pg_catalog.pg_index i
        JOIN pg_catalog.pg_class c  ON c.oid = i.indrelid
        JOIN pg_catalog.pg_class ic ON ic.oid = i.indexrelid
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
        ORDER BY n.nspname, c.relname, ic.relname
        """

    let viewDefinitions =
        """
        SELECT n.nspname AS schema_name,
               c.relname AS relation_name,
               COALESCE(pg_catalog.pg_get_viewdef(c.oid, true), '') AS definition
        FROM pg_catalog.pg_class c
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        WHERE c.relkind IN ('v', 'm')
          AND n.nspname NOT IN ('pg_catalog', 'information_schema')
        ORDER BY n.nspname, c.relname
        """

    let routines =
        """
        SELECT n.nspname AS schema_name,
               p.proname AS routine_name,
               p.prokind::text AS kind,
               -- The body AS STORED. PostgreSQL keeps a classic AS $$...$$
               -- body verbatim, so it compares directly against a declared one.
               -- Empty for a SQL-standard BEGIN ATOMIC body, which is stored as
               -- a parse tree instead, and that is reported as "no text" rather
               -- than as an empty body.
               COALESCE(p.prosrc, '') AS body,
               -- The IN argument TYPES, as an array, built from proargtypes.
               --
               -- Not `pg_get_function_arguments` and not
               -- `pg_get_function_identity_arguments`: both render parameter
               -- NAMES as well as types (`p_id bigint`), so neither matches a
               -- declared signature, and a name is not part of a routine's
               -- identity — overloads are resolved on types alone.
               --
               -- Not a rendered string split on commas either: a type may
               -- contain one. `numeric(12,2)` would split into two arguments
               -- and `f(numeric(12,2))` would never match itself.
               COALESCE(
                 (SELECT array_agg(pg_catalog.format_type(k.typid, NULL) ORDER BY k.ord)
                  FROM unnest(p.proargtypes) WITH ORDINALITY AS k(typid, ord)),
                 '{}') AS argument_types,
               pg_catalog.format_type(p.prorettype, NULL)  AS return_type,
               l.lanname AS language,
               (d.objid IS NOT NULL) AS extension_owned
        FROM pg_catalog.pg_proc p
        JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
        JOIN pg_catalog.pg_language l ON l.oid = p.prolang
        LEFT JOIN pg_catalog.pg_depend d
               ON d.objid = p.oid
              AND d.classid = 'pg_proc'::regclass
              AND d.deptype = 'e'
        WHERE p.prokind IN ('f', 'p')
          AND n.nspname NOT IN ('pg_catalog', 'information_schema')
        ORDER BY n.nspname, p.proname
        """

    let serverVersion = "SELECT current_setting('server_version')"

    let searchPath = "SELECT current_setting('search_path')"
