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
    ///
    /// Read because an EMPTY schema is invisible in the object list — it has
    /// nothing in it — so "no objects in `ref`" and "no schema called `ref`"
    /// would otherwise be the same observation, and they call for different
    /// DDL. This query existed unused until `readSchemas` needed it.
    let schemas =
        """
        SELECT n.nspname
        FROM pg_catalog.pg_namespace n
        WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
          AND n.nspname NOT LIKE 'pg_toast%'
          AND n.nspname NOT LIKE 'pg_temp%'
          AND n.nspname NOT LIKE 'pg_toast_temp%'
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
               COALESCE(pg_catalog.pg_get_expr(ad.adbin, ad.adrelid), '') AS default_expression,
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

    /// Triggers, excluding the ones PostgreSQL creates for itself.
    ///
    /// `tgisinternal` is the load-bearing filter: every foreign key is
    /// implemented as a pair of hidden triggers, so without it a table with
    /// two foreign keys would show four triggers nobody declared and a diff
    /// with drops enabled would propose removing the constraints by a side
    /// door.
    ///
    /// `tgtype` is decoded HERE rather than in F# because this is where the
    /// catalog's encoding belongs, and because it keeps one bit layout in the
    /// codebase instead of two. The bits are PostgreSQL's own, from
    /// `trigger.h`, and the same ones `CreateTrigStmt` uses on the parser
    /// side: ROW 1, BEFORE 2, INSERT 4, DELETE 8, UPDATE 16, TRUNCATE 32,
    /// INSTEAD 64. AFTER is the absence of BEFORE and INSTEAD, not a bit.
    ///
    /// The WHEN clause is reported as PRESENT or ABSENT and never as text:
    /// `pg_get_expr(tgqual, tgrelid)` fails outright with "expression contains
    /// variables of more than one relation", because OLD and NEW are two
    /// relations. Verified against a live server.
    let triggers =
        """
        SELECT n.nspname AS schema_name,
               c.relname AS relation_name,
               c.relkind::text AS relation_kind,
               t.tgname  AS trigger_name,
               CASE WHEN (t.tgtype & 64) <> 0 THEN 'instead'
                    WHEN (t.tgtype & 2)  <> 0 THEN 'before'
                    ELSE 'after' END AS timing,
               CASE WHEN (t.tgtype & 1) <> 0 THEN 'row' ELSE 'statement' END AS level,
               ARRAY(SELECT v.name
                     FROM (VALUES (4, 'insert'), (8, 'delete'), (16, 'update'), (32, 'truncate'))
                          AS v(bit, name)
                     WHERE (t.tgtype & v.bit) <> 0
                     ORDER BY v.bit) AS events,
               COALESCE(fn.nspname, '') AS function_schema,
               p.proname AS function_name,
               -- tgargs is a bytea of NUL-terminated C strings. Split on the
               -- NUL rather than on a comma: an argument may contain one, and
               -- splitting a rendered argument list would turn one argument
               -- into two.
               COALESCE(
                 (SELECT array_agg(a ORDER BY o)
                  FROM unnest(string_to_array(encode(t.tgargs, 'escape'), '\000'))
                       WITH ORDINALITY AS u(a, o)
                  WHERE o <= t.tgnargs),
                 '{}') AS arguments,
               COALESCE(
                 (SELECT array_agg(att.attname ORDER BY k.ord)
                  FROM unnest(t.tgattr::int2[]) WITH ORDINALITY AS k(attnum, ord)
                  JOIN pg_catalog.pg_attribute att
                    ON att.attrelid = t.tgrelid AND att.attnum = k.attnum),
                 '{}') AS update_columns,
               (t.tgqual IS NOT NULL) AS has_condition
        FROM pg_catalog.pg_trigger t
        JOIN pg_catalog.pg_class c ON c.oid = t.tgrelid
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        JOIN pg_catalog.pg_proc p ON p.oid = t.tgfoid
        LEFT JOIN pg_catalog.pg_namespace fn ON fn.oid = p.pronamespace
        WHERE NOT t.tgisinternal
          AND n.nspname NOT IN ('pg_catalog', 'information_schema')
        ORDER BY n.nspname, c.relname, t.tgname
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

    /// Sequences a project could have declared — and ONLY those.
    ///
    /// A `serial` column and an `IDENTITY` column each get a sequence of their
    /// own, and neither belongs to the project: the column owns it, and
    /// dropping it breaks the column. Both are excluded here, and it takes TWO
    /// dependency types to do it, which is the part that is easy to get wrong:
    ///
    ///   serial   -> pg_depend.deptype = 'a'  (auto)
    ///   identity -> pg_depend.deptype = 'i'  (internal)
    ///
    /// Verified against a live server. Filtering on 'a' alone — the obvious
    /// reading — leaves an identity column's sequence looking like a standalone
    /// one, and `--allow-drops` would then propose dropping the sequence that
    /// backs a live column.
    ///
    /// `last_value` is deliberately not selected. It is data: it moves on every
    /// nextval, so comparing it would report a difference on a sequence nobody
    /// touched, and acting on that would hand out a number twice.
    let sequences =
        """
        SELECT n.nspname AS schema_name,
               c.relname AS sequence_name,
               pg_catalog.format_type(s.seqtypid, NULL) AS data_type,
               s.seqstart     AS start_value,
               s.seqincrement AS increment_by,
               s.seqmin       AS min_value,
               s.seqmax       AS max_value,
               s.seqcache     AS cache_size,
               s.seqcycle     AS is_cycled,
               (dx.objid IS NOT NULL) AS extension_owned
        FROM pg_catalog.pg_class c
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        JOIN pg_catalog.pg_sequence s ON s.seqrelid = c.oid
        LEFT JOIN pg_catalog.pg_depend dx
               ON dx.objid = c.oid
              AND dx.classid = 'pg_class'::regclass
              AND dx.deptype = 'e'
        WHERE c.relkind = 'S'
          AND n.nspname NOT IN ('pg_catalog', 'information_schema')
          AND NOT EXISTS (
                SELECT 1 FROM pg_catalog.pg_depend d
                WHERE d.objid = c.oid
                  AND d.classid = 'pg_class'::regclass
                  AND d.deptype IN ('a', 'i'))
        ORDER BY n.nspname, c.relname
        """

    /// Privileges granted on relations and sequences.
    ///
    /// Three things here are not obvious, and each was verified against a live
    /// server because getting any of them wrong is destructive:
    ///
    ///   * The OWNER appears in the ACL with every privilege the moment
    ///     anything is granted (`postgres=arwdDxt/postgres`). Those are not
    ///     grants, and reading them as grants makes Strata propose revoking the
    ///     owner's own access to its own table. Excluded by grantee = relowner.
    ///
    ///   * PUBLIC is grantee OID 0, and `pg_get_userbyid(0)` returns the
    ///     literal string `unknown (OID=0)` rather than failing. Emitting a
    ///     revoke from that would name a role that does not exist, so it is
    ///     translated to PUBLIC here.
    ///
    ///   * `relacl` is NULL for an object nobody has granted on — the DEFAULT
    ///     state, not an empty one. `aclexplode` of NULL yields no rows, which
    ///     is the right answer, but only because there are genuinely no grants
    ///     to report.
    let grants =
        """
        SELECT n.nspname AS schema_name,
               c.relname AS object_name,
               CASE WHEN a.grantee = 0 THEN 'PUBLIC'
                    ELSE pg_catalog.pg_get_userbyid(a.grantee) END AS grantee,
               a.privilege_type
        FROM pg_catalog.pg_class c
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        CROSS JOIN LATERAL pg_catalog.aclexplode(c.relacl) a
        WHERE c.relkind IN ('r', 'p', 'v', 'm', 'S')
          AND n.nspname NOT IN ('pg_catalog', 'information_schema')
          AND a.grantee <> c.relowner
        ORDER BY n.nspname, c.relname, grantee, a.privilege_type
        """

    let serverVersion = "SELECT current_setting('server_version')"

    let searchPath = "SELECT current_setting('search_path')"
