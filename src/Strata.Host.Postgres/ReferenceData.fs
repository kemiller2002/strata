namespace Strata.Host.Postgres

open System
open Npgsql

/// Resolving declared reference rows against the rows a table actually holds.
///
/// Authority for: what a project's data files and a live table disagree about.
///
/// ## Why this needs the server
///
/// A file says `1.250` and a `numeric(12,2)` column holds `1.25`. It says
/// `'checking'` and a `varchar(20)` column holds `checking`. It says `1` and a
/// `bigint` column holds `1`. Deciding whether those are the SAME VALUE is a
/// question about PostgreSQL's type system, and the only thing that answers it
/// correctly is PostgreSQL.
///
/// So both sides are rendered by the server, through the same column types.
/// The declared literals are inserted into a shadow table created from the
/// real one — `CREATE TABLE ... AS SELECT <declared columns> ... WITH NO DATA`
/// copies exactly those columns with exactly those types — and both tables are
/// then read back through `ROW(...)::text`. Two rows that mean the same thing
/// render identically; two that differ, differ.
///
/// Strata never interprets a literal. It re-emits the token the author wrote
/// and lets the server do the rest, here and again at apply time.
///
/// ## This is the only place Strata reads DATA
///
/// Everything else in Strata reads catalog metadata and says so. Diffing
/// declared rows cannot be done without selecting them, so this module does —
/// and ONLY from tables the project declares rows for. It does not make Strata
/// a tool that inspects your data; it reads the reference tables you asked it
/// to manage, and nothing else.
///
/// ## Nothing is persisted
///
/// One transaction, always rolled back, exactly as `ShadowNormalisation` does.
/// The shadow table is created and discarded; the real table is only ever read.
/// The heaviest lock taken is the ACCESS SHARE that any SELECT takes.
module ReferenceData =

    /// One row, as the server renders it.
    type ResolvedRow =
        { /// `ROW(<key columns>)::text` — what makes two rows the same row.
          Key: string
          /// `ROW(<declared columns>)::text` — what makes them equal.
          Rendered: string
          /// Each declared column's value as re-injectable SQL, from
          /// `quote_nullable`. Empty for a row read from the real table, which
          /// never needs writing back.
          Literals: string list }

    /// What one declared data file resolved to.
    type Resolution =
        { Table: string
          Columns: string list
          /// The columns rows are matched ON — the table's primary key.
          KeyColumns: string list
          Declared: ResolvedRow list
          Deployed: ResolvedRow list }

    /// A data file that could not be resolved, and why.
    ///
    /// Separate from an empty resolution, and the distinction is the point: a
    /// table Strata could not read is not a table with no rows, and a caller
    /// that confused them would propose inserting every declared row into a
    /// table that already has them.
    type Failure = { Table: string; Reason: string }

    let private shadowSchema () =
        sprintf "_strata_shadow_%s" (Guid.NewGuid().ToString("N").Substring(0, 12))

    let private quoteIdent (name: string) = "\"" + name.Replace("\"", "\"\"") + "\""

    /// The primary key columns of a table, in key order.
    ///
    /// Rows are matched on the primary key because that is what the DATABASE
    /// says identifies a row. Strata does not invent an alternative: a table
    /// without a primary key has no row identity to match on, and guessing one
    /// would silently update the wrong row.
    let private keyColumnsSql =
        """
        SELECT a.attname
        FROM pg_catalog.pg_constraint con
        JOIN LATERAL unnest(con.conkey) WITH ORDINALITY AS k(attnum, ord) ON true
        JOIN pg_catalog.pg_attribute a
          ON a.attrelid = con.conrelid AND a.attnum = k.attnum
        WHERE con.conrelid = $1::regclass AND con.contype = 'p'
        ORDER BY k.ord
        """

    /// Resolve every declared data file against the live database.
    ///
    /// Returns what resolved and what did not. A file that fails leaves the
    /// caller unable to claim anything about that table, which is why failures
    /// are returned rather than dropped.
    /// `declaringDdl` is the table's own CREATE TABLE text, used only when the
    /// table does not exist yet — a first apply, where the rows and the table
    /// that holds them arrive in the same plan. Without it a new reference
    /// table would always need TWO applies: one to create it and one to fill
    /// it, with the first run reporting rows it had not written.
    let resolve
        (connectionString: string)
        /// Enum types the project declares, as `(qualified name, values)`.
        ///
        /// Created here when the database does not have them yet, inside the
        /// same always-rolled-back transaction. Without this, a reference table
        /// whose column uses a type that arrives in the SAME plan cannot be
        /// built in the shadow at all — `type "app.kind" does not exist` — so
        /// its rows are disclosed as not-compared and land a run later. Honest,
        /// and one run more than it needs to be.
        ///
        /// They are created under their REAL names rather than in the throwaway
        /// schema, because the declaring table's DDL names them that way and
        /// rewriting an author's text is what `WI-0094` was about. The
        /// transaction rolls back either way, so nothing survives it.
        (declaredEnums: (string * string list) list)
        (declared: (string * string list * string list list * string option) list)
        : Result<Resolution list * Failure list, string> =

        if List.isEmpty declared then Ok([], [])
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

                let queryStrings (sql: string) (parameter: string option) =
                    use command = new NpgsqlCommand(sql, connection, transaction)

                    match parameter with
                    | Some value -> command.Parameters.AddWithValue value |> ignore
                    | None -> ()

                    use reader = command.ExecuteReader()
                    [ while reader.Read() do
                        yield reader.GetString 0 ]

                let queryRows (sql: string) (withLiterals: bool) =
                    use command = new NpgsqlCommand(sql, connection, transaction)
                    use reader = command.ExecuteReader()

                    [ while reader.Read() do
                        yield
                            { Key = reader.GetString 0
                              Rendered = reader.GetString 1
                              Literals =
                                if withLiterals then
                                    reader.GetFieldValue<string array> 2 |> List.ofArray
                                else
                                    [] } ]

                let resolutions = ResizeArray<Resolution>()
                let failures = ResizeArray<Failure>()

                let exists (table: string) =
                    use command =
                        new NpgsqlCommand("SELECT to_regclass($1) IS NOT NULL", connection, transaction)

                    command.Parameters.AddWithValue table |> ignore
                    match command.ExecuteScalar() with
                    | :? bool as b -> b
                    | _ -> false

                // Before any table is built, because a table's columns may
                // be declared with one. A type the database already has is left
                // alone: re-creating it would fail, and the real one is what the
                // table will actually meet.
                for enumName, values in declaredEnums do
                    let savepoint = "sp_" + Guid.NewGuid().ToString("N").Substring(0, 8)

                    try
                        exec (sprintf "SAVEPOINT %s" savepoint)

                        use command =
                            new NpgsqlCommand("SELECT to_regtype($1) IS NOT NULL", connection, transaction)

                        command.Parameters.AddWithValue enumName |> ignore

                        let present =
                            match command.ExecuteScalar() with
                            | :? bool as b -> b
                            | _ -> false

                        if not present then
                            exec (
                                sprintf
                                    "CREATE TYPE %s AS ENUM (%s)"
                                    enumName
                                    (values
                                     |> List.map (fun v -> "'" + v.Replace("'", "''") + "'")
                                     |> String.concat ", ")
                            )
                    with _ ->
                        // A type that could not be created is not fatal: the
                        // tables that need it fail on their own savepoint below
                        // and report why, which is a better message than
                        // anything this loop could invent.
                        try exec (sprintf "ROLLBACK TO SAVEPOINT %s" savepoint) with _ -> ()

                for table, columns, rows, declaringDdl in declared do
                    let savepoint = "sp_" + Guid.NewGuid().ToString("N").Substring(0, 8)

                    try
                        exec (sprintf "SAVEPOINT %s" savepoint)

                        let tableExists = exists table

                        // A schema of its own per table, so the shadow table
                        // can carry the REAL table's name without two of them
                        // colliding. That name is not cosmetic: PostgreSQL
                        // names the constraints and indexes that LIKE copies
                        // after the new table, so naming the shadow
                        // `account_type` makes its unique constraint
                        // `account_type_code_key` — the same name the real one
                        // has. A conflict then reports the constraint the user
                        // can actually go and look at, instead of a generated
                        // name that means nothing outside this transaction.
                        let tableSchema = sprintf "%s_%s" schema (Guid.NewGuid().ToString("N").Substring(0, 8))
                        exec (sprintf "CREATE SCHEMA %s" tableSchema)

                        let shadowTable =
                            let objectName =
                                match table.Split('.') with
                                | [| _; object' |] -> object'
                                | _ -> table

                            sprintf "%s.%s" tableSchema (quoteIdent objectName)

                        // The shadow copies the real table INCLUDING ALL, so
                        // declared rows meet the same primary key, unique
                        // constraints, checks and defaults the real table has.
                        //
                        // The declared rows go in first and the undeclared
                        // deployed ones follow, so the shadow ends up holding
                        // the state an apply would PRODUCE. Every constraint
                        // fires against that: two declarations sharing a unique
                        // value, one failing a CHECK, or a declaration
                        // colliding with a row the project does not declare all
                        // fail here, with the server's own message, instead of
                        // halfway through an apply.
                        let built =
                            if tableExists then
                                exec (sprintf "CREATE TABLE %s (LIKE %s INCLUDING ALL)" shadowTable table)
                                true
                            else
                                match declaringDdl with
                                | None -> false
                                | Some ddl ->
                                    // The table arrives in this same plan, so
                                    // there is nothing to copy from. Its own
                                    // declaring file is the next best thing and
                                    // is exactly what the apply will execute.
                                    //
                                    // Through the scanner, not `IndexOf '('`.
                                    // This file escaped WI-0092 because that
                                    // one fixed ShadowNormalisation, and the
                                    // identical search lived here too: one
                                    // leading comment containing a parenthesis
                                    // — `-- bins (aisle, shelf)`, or a
                                    // `strata:renamed_from` annotation — put
                                    // the split inside the comment and the
                                    // shadow CREATE TABLE became garbage, so
                                    // the rows "could not be checked".
                                    let bodyStart =
                                        ShadowNormalisation.tableBodyStart ddl |> Option.defaultValue -1

                                    if bodyStart < 0 then false
                                    else
                                        exec (sprintf "CREATE TABLE %s %s" shadowTable (ddl.Substring bodyStart))
                                        true

                        if not built then
                            failures.Add
                                { Table = table
                                  Reason =
                                    "does not exist yet and the project does not carry a single-statement CREATE TABLE for it, so its declared rows could not be checked" }
                        else

                        // Read the key from whichever table actually exists.
                        // For a new table that is the shadow, which was built
                        // from the same DDL the apply will run.
                        let keyColumns =
                            queryStrings keyColumnsSql (Some(if tableExists then table else shadowTable))

                        if List.isEmpty keyColumns then
                            failures.Add
                                { Table = table
                                  Reason =
                                    "has no primary key, so there is nothing to match a declared row against a deployed one. Strata will not guess a row identity." }
                        else

                        let missingKey =
                            keyColumns
                            |> List.filter (fun k ->
                                not (columns |> List.exists (fun c -> c.ToLowerInvariant() = k.ToLowerInvariant())))

                        if not (List.isEmpty missingKey) then
                            failures.Add
                                { Table = table
                                  Reason =
                                    sprintf
                                        "declares rows without its key column(s) %s, so no declared row can be matched to a deployed one"
                                        (String.concat ", " missingKey) }
                        else

                        let quotedColumns = columns |> List.map quoteIdent
                        let columnList = String.concat ", " quotedColumns
                        let keyList = keyColumns |> List.map quoteIdent |> String.concat ", "

                        let values =
                            rows
                            |> List.map (fun row -> "(" + String.concat ", " row + ")")
                            |> String.concat ", "

                        exec (sprintf "INSERT INTO %s (%s) VALUES %s" shadowTable columnList values)

                        let renderSql (source: string) (withLiterals: bool) =
                            sprintf
                                "SELECT ROW(%s)::text, ROW(%s)::text%s FROM %s"
                                keyList
                                columnList
                                (if withLiterals then
                                     ", ARRAY["
                                     + (quotedColumns
                                        |> List.map (fun c -> sprintf "pg_catalog.quote_nullable(%s)" c)
                                        |> String.concat ", ")
                                     + "]"
                                 else
                                     "")
                                source

                        let declaredRows = queryRows (renderSql shadowTable true) true

                        // A table that does not exist has no rows. That is a
                        // fact, not a failure to read one, and the difference
                        // matters: every declared row is then an insert.
                        let deployedRows =
                            if tableExists then queryRows (renderSql table false) false else []

                        // The shadow so far holds only the DECLARED rows, so
                        // it catches two declarations colliding but not a
                        // declaration colliding with a row already in the
                        // table that the project does not declare. Copying the
                        // undeclared deployed rows in makes the shadow hold the
                        // state an apply would PRODUCE — declared rows where
                        // the keys match, existing rows everywhere else — and
                        // every constraint then fires against that.
                        //
                        // The row values never leave the server: this is an
                        // INSERT ... SELECT, so there is nothing to re-render
                        // and nothing to re-quote. The cost is one more pass
                        // over a table whose rows were already being read.
                        let conflict =
                            if not tableExists then None
                            else
                                try
                                    exec (
                                        sprintf
                                            "INSERT INTO %s (%s) SELECT %s FROM %s t \
                                             WHERE NOT EXISTS (SELECT 1 FROM %s s WHERE ROW(%s) = ROW(%s))"
                                            shadowTable
                                            columnList
                                            (quotedColumns |> List.map (fun c -> "t." + c) |> String.concat ", ")
                                            table
                                            shadowTable
                                            (keyColumns |> List.map (fun k -> "s." + quoteIdent k) |> String.concat ", ")
                                            (keyColumns |> List.map (fun k -> "t." + quoteIdent k) |> String.concat ", "))

                                    None
                                with ex ->
                                    // The transaction is poisoned until the
                                    // savepoint is released. Everything needed
                                    // was already read into F# values, so
                                    // discarding the shadow costs nothing.
                                    try exec (sprintf "ROLLBACK TO SAVEPOINT %s" savepoint) with _ -> ()
                                    Some ex.Message

                        match conflict with
                        | Some reason ->
                            // Proposing the inserts anyway would produce a plan
                            // that cannot run. Reporting the conflict and
                            // proposing nothing is the honest outcome.
                            failures.Add
                                { Table = table
                                  Reason =
                                    sprintf
                                        "declared rows conflict with rows already in the table that the project does not declare: %s"
                                        reason }
                        | None ->

                        resolutions.Add
                            { Table = table
                              Columns = columns
                              KeyColumns = keyColumns
                              Declared = declaredRows
                              Deployed = deployedRows }
                    with ex ->
                        try exec (sprintf "ROLLBACK TO SAVEPOINT %s" savepoint) with _ -> ()
                        failures.Add { Table = table; Reason = ex.Message }

                transaction.Rollback()
                Ok(List.ofSeq resolutions, List.ofSeq failures)
            with ex ->
                try transaction.Rollback() with _ -> ()
                Error ex.Message
        with ex ->
            Error ex.Message
