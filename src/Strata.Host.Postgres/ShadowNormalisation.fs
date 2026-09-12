namespace Strata.Host.Postgres

open System
open Npgsql

/// Normalising declared DDL by asking the server what it means.
///
/// Authority for: turning a project's DDL text into the form the catalog would
/// report for it.
///
/// ## Why this exists
///
/// A view's declared text and its catalog form never match. PostgreSQL parses
/// a view and stores a rewritten tree; `pg_get_viewdef` deparses that back,
/// schema-qualified and reformatted. `SELECT id FROM app.orders WHERE status =
/// 'open'` comes back as a multi-line statement with `::text` casts. Comparing
/// the two texts reports a difference on every run for a view nobody touched,
/// which is why `SchemaDiff` could only disclose view definitions as
/// not-compared.
///
/// The only thing that can normalise PostgreSQL DDL faithfully is PostgreSQL.
/// So the declared DDL is executed in a throwaway schema and read back through
/// the same catalog function the real object is read through. Both sides then
/// carry the server's own rendering and compare exactly.
///
/// ## Nothing is persisted
///
/// Everything happens inside ONE transaction that is ALWAYS rolled back, on
/// success and on failure alike. PostgreSQL has transactional DDL, so a
/// rolled-back `CREATE SCHEMA` leaves no trace — no schema, no objects, no
/// sequence, nothing in the catalog. `plan` stays effectively read-only: it
/// writes only what it immediately discards.
///
/// This is the one place in Strata that issues a statement other than SELECT,
/// which is why it is a module of its own rather than a helper inside
/// introspection. `CatalogQueries` documents that every query there is a
/// SELECT; that claim stays true.
///
/// ## When it cannot run
///
/// A role without CREATE, a read-only replica, or DDL the server rejects all
/// make normalisation impossible. Every one of those returns `None` and the
/// caller falls back to reporting the definition as not-compared. Degrading to
/// "I could not check" is correct; guessing is not.
module ShadowNormalisation =

    /// A schema name no project would use, unique per call so two concurrent
    /// plans cannot collide.
    let private shadowSchema () =
        sprintf "_strata_shadow_%s" (Guid.NewGuid().ToString("N").Substring(0, 12))

    /// What the server made of a declared table.
    type TableNormalisation =
        { Table: string
          /// Column name -> default expression, as the catalog renders it.
          Defaults: (string * string) list
          /// Constraint name -> check definition, as the catalog renders it.
          Checks: (string * string) list }

    /// Normalise declared view DDL into the catalog's rendering.
    ///
    /// Takes `(name, ddl)` pairs and returns `(name, normalisedDefinition)` for
    /// those the server accepted. A view whose DDL fails is simply absent from
    /// the result — the caller cannot then claim it matches, which is the
    /// honest outcome for DDL the server will not take.
    let normaliseViews
        (connectionString: string)
        (views: (string * string) list)
        : Result<(string * string) list, string> =

        if List.isEmpty views then Ok []
        else

        try
            use connection = new NpgsqlConnection(connectionString)
            connection.Open()
            use transaction = connection.BeginTransaction()

            try
                let schema = shadowSchema ()

                let exec (sql: string) =
                    use command = new NpgsqlCommand(sql, connection, transaction)
                    command.ExecuteNonQuery() |> ignore

                exec (sprintf "CREATE SCHEMA %s" schema)

                // The shadow view is created in the throwaway schema, but its
                // BODY still references the real objects, so the server
                // deparses it exactly as it would the real view. Running with
                // the same search_path the session already has keeps
                // qualification decisions identical on both sides.
                let normalised =
                    views
                    |> List.choose (fun (name, ddl) ->
                        // A savepoint per view: one rejected definition must not
                        // abort the transaction and lose the others.
                        let savepoint = "sp_" + Guid.NewGuid().ToString("N").Substring(0, 8)

                        try
                            exec (sprintf "SAVEPOINT %s" savepoint)

                            let shadowName =
                                sprintf "%s.v_%s" schema (Guid.NewGuid().ToString("N").Substring(0, 12))

                            // Replace the declared object name with the shadow
                            // one by wrapping the body, rather than rewriting
                            // the author's text: `CREATE VIEW x AS <body>` is
                            // reconstructed as `CREATE VIEW shadow AS <body>`.
                            let bodyStart = ddl.IndexOf(" AS ", StringComparison.OrdinalIgnoreCase)

                            if bodyStart < 0 then None
                            else
                                let body = ddl.Substring(bodyStart + 4).TrimEnd().TrimEnd(';')
                                exec (sprintf "CREATE VIEW %s AS %s" shadowName body)

                                use command =
                                    new NpgsqlCommand(
                                        sprintf
                                            "SELECT pg_catalog.pg_get_viewdef('%s'::regclass, true)"
                                            (shadowName.Replace("'", "''")),
                                        connection,
                                        transaction)

                                match command.ExecuteScalar() with
                                | :? string as definition -> Some(name, definition)
                                | _ -> None
                        with _ ->
                            try exec (sprintf "ROLLBACK TO SAVEPOINT %s" savepoint) with _ -> ()
                            None)

                // ALWAYS. There is no success path that commits.
                transaction.Rollback()
                Ok normalised
            with ex ->
                try transaction.Rollback() with _ -> ()
                Error ex.Message
        with ex ->
            Error ex.Message

    /// Normalise declared table DDL, returning the catalog's rendering of its
    /// defaults and check constraints.
    ///
    /// `DEFAULT 'open'` is stored and reported as `'open'::text`, and
    /// `CHECK (balance >= 0)` as `CHECK ((balance >= (0)::numeric))`. Neither
    /// matches what the author wrote, which is why both were disclosed rather
    /// than compared. Round-tripping the declared DDL through the server puts
    /// both sides in the same form.
    ///
    /// Same transaction discipline as views: one transaction, always rolled
    /// back, a savepoint per table so one rejected definition does not lose the
    /// rest.
    let normaliseTables
        (connectionString: string)
        (tables: (string * string) list)
        : Result<TableNormalisation list, string> =

        if List.isEmpty tables then Ok []
        else

        try
            use connection = new NpgsqlConnection(connectionString)
            connection.Open()
            use transaction = connection.BeginTransaction()

            try
                let schema = shadowSchema ()

                let exec (sql: string) =
                    use command = new NpgsqlCommand(sql, connection, transaction)
                    command.ExecuteNonQuery() |> ignore

                let queryPairs (sql: string) =
                    use command = new NpgsqlCommand(sql, connection, transaction)
                    use reader = command.ExecuteReader()

                    [ while reader.Read() do
                        if not (reader.IsDBNull 0) && not (reader.IsDBNull 1) then
                            yield reader.GetString 0, reader.GetString 1 ]

                exec (sprintf "CREATE SCHEMA %s" schema)

                let normalised =
                    tables
                    |> List.choose (fun (name, ddl) ->
                        let savepoint = "sp_" + Guid.NewGuid().ToString("N").Substring(0, 8)

                        try
                            exec (sprintf "SAVEPOINT %s" savepoint)

                            // The column list starts at the first `(`. Anything
                            // before it is the name, which is replaced by the
                            // shadow one; the author's body is untouched.
                            // `CREATE TABLE x AS SELECT ...` has no such body
                            // and is skipped rather than mangled.
                            let bodyStart = ddl.IndexOf '('

                            if bodyStart < 0 then None
                            else
                                let shadowName =
                                    sprintf "%s.t_%s" schema (Guid.NewGuid().ToString("N").Substring(0, 12))

                                exec (sprintf "CREATE TABLE %s %s" shadowName (ddl.Substring bodyStart))

                                let quoted = shadowName.Replace("'", "''")

                                let defaults =
                                    queryPairs (
                                        sprintf
                                            """
                                            SELECT a.attname,
                                                   pg_catalog.pg_get_expr(d.adbin, d.adrelid)
                                            FROM pg_catalog.pg_attribute a
                                            JOIN pg_catalog.pg_attrdef d
                                              ON d.adrelid = a.attrelid AND d.adnum = a.attnum
                                            WHERE a.attrelid = '%s'::regclass AND a.attnum > 0
                                            """
                                            quoted)

                                let checks =
                                    queryPairs (
                                        sprintf
                                            """
                                            SELECT con.conname,
                                                   pg_catalog.pg_get_constraintdef(con.oid)
                                            FROM pg_catalog.pg_constraint con
                                            WHERE con.conrelid = '%s'::regclass AND con.contype = 'c'
                                            """
                                            quoted)

                                Some { Table = name; Defaults = defaults; Checks = checks }
                        with _ ->
                            try exec (sprintf "ROLLBACK TO SAVEPOINT %s" savepoint) with _ -> ()
                            None)

                transaction.Rollback()
                Ok normalised
            with ex ->
                try transaction.Rollback() with _ -> ()
                Error ex.Message
        with ex ->
            Error ex.Message

    /// One declared policy, as the catalog would render it.
    type PolicyNormalisation =
        { Table: string
          Policy: string
          /// The `USING` expression as the catalog renders it, or `None` when
          /// the policy has no such clause. An `INSERT` policy never does.
          Using: string option
          /// The `WITH CHECK` expression, or `None` when there is no clause.
          WithCheck: string option }

    /// Normalise declared policy DDL into the catalog's rendering.
    ///
    /// A policy's expression is over the table's columns, so the table has to
    /// exist before the policy can be created — and it has to exist under its
    /// REAL name, because `CREATE POLICY p ON orders` names it. So each table
    /// gets a shadow SCHEMA of its own and the table keeps its name inside it,
    /// the same trick reference-data resolution uses; `search_path` then makes
    /// the declaring text run exactly as written.
    ///
    /// Takes one entry per table: the table's own declared DDL, and the
    /// verbatim text of each policy on it. A policy whose DDL the server
    /// rejects is absent from the result rather than guessed at, and the caller
    /// then reports it as not-compared instead of claiming it matches.
    let normalisePolicies
        (connectionString: string)
        (tables: (string * string * (string * string) list) list)
        : Result<PolicyNormalisation list, string> =

        if List.isEmpty tables then Ok []
        else

        try
            use connection = new NpgsqlConnection(connectionString)
            connection.Open()
            use transaction = connection.BeginTransaction()

            try
                let exec (sql: string) =
                    use command = new NpgsqlCommand(sql, connection, transaction)
                    command.ExecuteNonQuery() |> ignore

                let normalised =
                    tables
                    |> List.collect (fun (table, tableDdl, policies) ->
                        let schema = shadowSchema ()
                        let savepoint = "sp_" + Guid.NewGuid().ToString("N").Substring(0, 8)

                        try
                            exec (sprintf "SAVEPOINT %s" savepoint)
                            exec (sprintf "CREATE SCHEMA %s" schema)

                            // The table keeps its own name inside the shadow
                            // schema, so the policy text that names it needs no
                            // rewriting — and rewriting is exactly where a
                            // normaliser stops normalising the thing the author
                            // wrote.
                            let bare =
                                match table.LastIndexOf '.' with
                                | -1 -> table
                                | i -> table.Substring(i + 1)

                            let bodyStart = tableDdl.IndexOf '('

                            if bodyStart < 0 then
                                try exec (sprintf "ROLLBACK TO SAVEPOINT %s" savepoint) with _ -> ()
                                []
                            else

                            exec (sprintf "SET LOCAL search_path TO %s" schema)
                            exec (sprintf "CREATE TABLE %s.\"%s\" %s" schema bare (tableDdl.Substring bodyStart))

                            // A project's files qualify their table names, so
                            // the policy text says `ON pol.doc` and
                            // `search_path` alone never reaches the shadow. The
                            // qualified name is rewritten to the shadow's;
                            // nothing else in the text is touched.
                            //
                            // Every spelling of the same name has to go, because
                            // a file may write any of them and missing one puts
                            // us back where this started — silently rendering
                            // nothing and reporting a match.
                            let rewriteTable (text: string) =
                                let schemaPart, tablePart =
                                    match table.LastIndexOf '.' with
                                    | -1 -> "", table
                                    | i -> table.Substring(0, i), table.Substring(i + 1)

                                let target = sprintf "%s.\"%s\"" schema bare

                                if schemaPart = "" then
                                    text
                                else
                                    [ sprintf "\"%s\".\"%s\"" schemaPart tablePart
                                      sprintf "\"%s\".%s" schemaPart tablePart
                                      sprintf "%s.\"%s\"" schemaPart tablePart
                                      sprintf "%s.%s" schemaPart tablePart ]
                                    |> List.fold
                                        (fun (acc: string) spelling -> acc.Replace(spelling, target))
                                        text

                            let results =
                                policies
                                |> List.choose (fun (policyName, policyDdl) ->
                                    let inner = "sp_" + Guid.NewGuid().ToString("N").Substring(0, 8)

                                    try
                                        exec (sprintf "SAVEPOINT %s" inner)
                                        exec (rewriteTable policyDdl)

                                        use command =
                                            new NpgsqlCommand(
                                                sprintf
                                                    """
                                                    SELECT pg_catalog.pg_get_expr(p.polqual, p.polrelid),
                                                           pg_catalog.pg_get_expr(p.polwithcheck, p.polrelid)
                                                    FROM pg_catalog.pg_policy p
                                                    JOIN pg_catalog.pg_class c ON c.oid = p.polrelid
                                                    JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                                                    WHERE n.nspname = '%s' AND p.polname = '%s'
                                                    """
                                                    (schema.Replace("'", "''"))
                                                    (policyName.Replace("'", "''")),
                                                connection,
                                                transaction
                                            )

                                        use reader = command.ExecuteReader()

                                        if reader.Read() then
                                            Some
                                                { Table = table
                                                  Policy = policyName
                                                  Using = (if reader.IsDBNull 0 then None else Some(reader.GetString 0))
                                                  WithCheck =
                                                    (if reader.IsDBNull 1 then None else Some(reader.GetString 1)) }
                                        else
                                            None
                                    with _ ->
                                        try exec (sprintf "ROLLBACK TO SAVEPOINT %s" inner) with _ -> ()
                                        None)

                            exec "RESET search_path"
                            results
                        with _ ->
                            try exec (sprintf "ROLLBACK TO SAVEPOINT %s" savepoint) with _ -> ()
                            [])

                transaction.Rollback()
                Ok normalised
            with ex ->
                try transaction.Rollback() with _ -> ()
                Error ex.Message
        with ex ->
            Error ex.Message
