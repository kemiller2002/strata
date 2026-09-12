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

    /// If a construct that hides text from SQL's lexer starts at `i`, the offset
    /// just past it.
    ///
    /// Authority for: which parts of a SQL string are text and which are code.
    ///
    /// The four things PostgreSQL's lexer hides: a line comment, a block comment
    /// (which NESTS, unlike C), single- and double-quoted text with doubled
    /// quotes as the escape, and a dollar-quoted string. Everything else is
    /// code, and everything that reshapes DDL in this module has to know the
    /// difference.
    ///
    /// `None` means `i` is ordinary code.
    let private hiddenEnd (sql: string) (i: int) : int option =
        let n = sql.Length

        // `$tag$` opens a dollar-quoted string and `$$` is the empty tag.
        // Anything else beginning with `$` is an ordinary character —
        // PostgreSQL identifiers may contain one.
        let dollarTag () =
            let rec tagEnd j =
                if j >= n then None
                elif sql.[j] = '$' then Some j
                elif Char.IsLetterOrDigit sql.[j] || sql.[j] = '_' then tagEnd (j + 1)
                else None

            tagEnd (i + 1) |> Option.map (fun j -> sql.Substring(i, j - i + 1))

        let rec blockEnd j level =
            if j >= n then n
            elif j + 1 < n && sql.[j] = '/' && sql.[j + 1] = '*' then blockEnd (j + 2) (level + 1)
            elif j + 1 < n && sql.[j] = '*' && sql.[j + 1] = '/' then
                if level = 1 then j + 2 else blockEnd (j + 2) (level - 1)
            else
                blockEnd (j + 1) level

        let rec quotedEnd j quote =
            if j >= n then n
            elif sql.[j] = quote then
                // A doubled quote is an escaped quote, not the end.
                if j + 1 < n && sql.[j + 1] = quote then quotedEnd (j + 2) quote else j + 1
            else
                quotedEnd (j + 1) quote

        if i >= n then
            None
        elif i + 1 < n && sql.[i] = '-' && sql.[i + 1] = '-' then
            match sql.IndexOf('\n', i) with
            | -1 -> Some n
            | e -> Some(e + 1)
        elif i + 1 < n && sql.[i] = '/' && sql.[i + 1] = '*' then
            Some(blockEnd (i + 2) 1)
        elif sql.[i] = '\'' || sql.[i] = '"' then
            Some(quotedEnd (i + 1) sql.[i])
        elif sql.[i] = '$' then
            match dollarTag () with
            | Some tag ->
                match sql.IndexOf(tag, i + tag.Length, StringComparison.Ordinal) with
                | -1 -> Some n
                | e -> Some(e + tag.Length)
            | None -> None
        else
            None

    /// The first offset in a SQL string where a predicate holds, considering
    /// only text that `hiddenEnd` does not hide, and telling the predicate what
    /// parenthesis depth it is looking at.
    ///
    /// Authority for: where a piece of SQL text can be split without a parser.
    ///
    /// ## Why this is worth the lines
    ///
    /// Everything in this module reshapes declared DDL into a statement the
    /// shadow schema can execute, and every reshaping needs an offset: where a
    /// view's body begins, where a table's column list begins. Both were found
    /// with a naive substring search, and both were wrong the same way — the
    /// search does not know that a match inside a comment, a quoted name or a
    /// string is not the thing it is looking for, nor that a match at the wrong
    /// nesting depth is a different thing entirely.
    ///
    /// `ddl.IndexOf(" AS ")` shipped and cost real comparisons. It wants a space
    /// on BOTH sides, so `CREATE VIEW v AS` followed by a newline — the way
    /// almost every view is written — never matched: the view was dropped from
    /// normalisation and a deployed body could drift from its file with a clean
    /// plan to show for it. `ddl.IndexOf '('` had the same shape and a more
    /// ordinary trigger: one leading comment containing a parenthesis, such as
    /// `-- products (catalogue)` or a `-- strata:renamed_from (shop.item)`
    /// annotation, silently took the check constraints, the column defaults, the
    /// policy expressions AND the view out of the comparison at once. Measured,
    /// not supposed: four disclosures appeared and nothing was proposed.
    ///
    /// The tier rules keep the parser out of this project
    /// (`DF-STRATA-2026-D3F8`: the catalog adapter does not parse SQL), so the
    /// answer cannot be "use the parse tree". It can be a scanner that knows the
    /// four things PostgreSQL's lexer knows, which is this.
    ///
    /// The depth passed to the predicate is the depth BEFORE the character is
    /// applied, so the `(` that opens a column list reads as depth zero.
    ///
    /// Written as direct recursion rather than a `seq { yield! }` generator on
    /// purpose: recursive `yield!` composes one enumerator per character, so
    /// reading the sequence is quadratic in the length of the DDL. A table
    /// declaration of a few thousand characters is ordinary.
    let private scanFor (isMatch: int -> char -> int -> bool) (sql: string) : int option =
        let n = sql.Length

        let rec scan i depth =
            if i >= n then
                None
            else
                match hiddenEnd sql i with
                | Some e -> scan e depth
                | None ->
                    if isMatch i sql.[i] depth then
                        Some i
                    else
                        let next =
                            if sql.[i] = '(' then depth + 1
                            elif sql.[i] = ')' then max 0 (depth - 1)
                            else depth

                        scan (i + 1) next

        scan 0 0

    /// Replace any of `spellings` with `target`, but ONLY where the text is code.
    ///
    /// Authority for: rewriting a name in declared SQL without rewriting the
    /// data.
    ///
    /// A policy's expression can legitimately contain the name of its own table
    /// as a string: `USING (note <> 'see shop.product')`. Rewriting that to the
    /// shadow's name changes what the policy MEANS, so the shadow renders a
    /// different expression than the file declares, and the two compare unequal
    /// on every run — a policy the author never touched, proposed for
    /// replacement forever. The same applies to a name inside a comment.
    ///
    /// Longest spelling first, so `"a"."b"` is never half-matched by `a.b`.
    ///
    /// A match is tried BEFORE the text is checked for hiding, and the order is
    /// the whole difference between a name and a string. A double-quoted
    /// identifier is CODE — `"shop"."product"` is the name, spelled carefully —
    /// so a spelling that begins with a quote has to be allowed to match it. A
    /// single-quoted or dollar-quoted string is DATA, and no spelling begins
    /// with those, so trying first costs nothing and the literal is then skipped
    /// whole. A quoted identifier that is NOT the name is skipped whole too, so
    /// a table called `"my shop.product notes"` keeps its own name.
    let replaceSignificant (spellings: string list) (target: string) (sql: string) : string =
        let n = sql.Length
        let ordered = spellings |> List.sortByDescending String.length

        let matchAt i =
            ordered
            |> List.tryFind (fun spelling ->
                i + spelling.Length <= n && String.CompareOrdinal(sql, i, spelling, 0, spelling.Length) = 0)

        let rec go i (acc: string list) =
            if i >= n then
                acc |> List.rev |> String.concat ""
            else
                match matchAt i with
                | Some spelling -> go (i + spelling.Length) (target :: acc)
                | None ->
                    match hiddenEnd sql i with
                    | Some e -> go e (sql.Substring(i, e - i) :: acc)
                    | None -> go (i + 1) (string sql.[i] :: acc)

        go 0 []

    /// PostgreSQL identifiers may contain `$`, so a word boundary is not
    /// `Char.IsLetterOrDigit` alone.
    let private isWordChar c = Char.IsLetterOrDigit c || c = '_' || c = '$'

    /// Where a view's body starts: the offset just past the first `AS` KEYWORD.
    ///
    /// `AS` counts only as a bare word at parenthesis depth zero and outside
    /// everything `scanFor` hides. That is what rules out the column list in
    /// `CREATE VIEW v (a, b) AS`, the options in `WITH (security_barrier)`, a
    /// view named `"my AS view"`, and an `AS` in a leading comment — each of
    /// which a looser match takes for the keyword and then hands the server a
    /// fragment.
    ///
    /// `None` means no such keyword was found, and the caller must not guess: a
    /// view Strata cannot split is a view it cannot normalise, which is
    /// disclosed rather than assumed to match.
    let viewBodyStart (ddl: string) : int option =
        let n = ddl.Length

        ddl
        |> scanFor (fun i c depth ->
            depth = 0
            && (c = 'a' || c = 'A')
            && i + 1 < n
            && (ddl.[i + 1] = 's' || ddl.[i + 1] = 'S')
            && (i = 0 || not (isWordChar ddl.[i - 1]))
            && (i + 2 >= n || not (isWordChar ddl.[i + 2])))
        |> Option.map (fun i -> i + 2)

    /// Where a view's NAME ends: the offset just past the object name that
    /// follows the `VIEW` keyword.
    ///
    /// Authority for: which part of a view declaration is the name, so the rest
    /// can be carried over verbatim.
    ///
    /// ## Why the rest has to be carried over
    ///
    /// `CREATE VIEW v (a, b) AS SELECT id, weight` was reconstructed for the
    /// shadow as `CREATE VIEW <shadow> AS SELECT id, weight`, dropping the
    /// column-alias list. That looks harmless and is not: `pg_get_viewdef`
    /// FOLDS the list into the query it renders, so the deployed view comes back
    /// as `SELECT id AS a, weight AS b` and the shadow as `SELECT id, weight`.
    /// The two never match, so Strata proposes `replace-view` on every run
    /// forever — the same shape as WI-0054, where an unnamed constraint could
    /// never converge.
    ///
    /// The `WITH (...)` options sit in the same span and are carried for the
    /// same reason: whatever the author wrote between the name and the keyword
    /// belongs to the view, and guessing which parts matter is how the first
    /// version got it wrong.
    ///
    /// `None` when there is no `VIEW` keyword or no name after it.
    let viewNameEnd (ddl: string) : int option =
        let n = ddl.Length

        // The `VIEW` keyword itself, as a bare word at depth zero — so a view
        // named `"view"` does not match its own name.
        let keyword =
            ddl
            |> scanFor (fun i c depth ->
                depth = 0
                && (c = 'v' || c = 'V')
                && i + 3 < n
                && String.Equals(ddl.Substring(i, 4), "VIEW", StringComparison.OrdinalIgnoreCase)
                && (i = 0 || not (isWordChar ddl.[i - 1]))
                && (i + 4 >= n || not (isWordChar ddl.[i + 4])))

        // A qualified name is one or more parts separated by dots, each either
        // quoted or a bare word.
        let rec skipSpace i =
            if i < n && Char.IsWhiteSpace ddl.[i] then skipSpace (i + 1) else i

        let rec endOfQuoted i =
            if i >= n then None
            elif ddl.[i] = '"' then
                if i + 1 < n && ddl.[i + 1] = '"' then endOfQuoted (i + 2) else Some(i + 1)
            else
                endOfQuoted (i + 1)

        let rec endOfWord i =
            if i < n && isWordChar ddl.[i] then endOfWord (i + 1) else i

        let rec namePart i =
            let i = skipSpace i

            let partEnd =
                if i < n && ddl.[i] = '"' then endOfQuoted (i + 1)
                elif i < n && isWordChar ddl.[i] then
                    let e = endOfWord i
                    if e = i then None else Some e
                else
                    None

            match partEnd with
            | None -> None
            | Some e ->
                // A dot continues the name; anything else ends it.
                let afterDot = skipSpace e
                if afterDot < n && ddl.[afterDot] = '.' then namePart (afterDot + 1) else Some e

        keyword |> Option.bind (fun k -> namePart (k + 4))

    /// What a view declaration says between its name and its `AS`: a column
    /// alias list, view options, or nothing.
    ///
    /// Returned verbatim, whitespace and comments included, so the shadow
    /// declaration is the author's text with only the name changed.
    let viewNameSuffix (ddl: string) : string =
        match viewNameEnd ddl, viewBodyStart ddl with
        // `viewBodyStart` points just past the two characters of `AS`.
        | Some nameEnd, Some bodyStart when bodyStart - 2 > nameEnd ->
            ddl.Substring(nameEnd, bodyStart - 2 - nameEnd).Trim()
        | _ -> ""

    /// The first bare word in a piece of SQL, skipping whatever hides text.
    ///
    /// Which statement kind this is, in other words — and a leading comment must
    /// not answer that question. `-- create the orders view` above a `SELECT`
    /// would make a naive reader call it a CREATE, which for `TypeCheck` is the
    /// difference between "cannot be type-checked" and "type-checks clean".
    ///
    /// `None` for text that is entirely comment or whitespace.
    let firstWord (sql: string) : string option =
        let n = sql.Length

        sql
        |> scanFor (fun _ c _ -> not (Char.IsWhiteSpace c))
        |> Option.bind (fun start ->
            if not (isWordChar sql.[start]) then
                None
            else
                let rec wordEnd i =
                    if i < n && isWordChar sql.[i] then wordEnd (i + 1) else i

                let e = wordEnd start
                if e > start then Some(sql.Substring(start, e - start)) else None)

    /// Where a table's column list starts: the offset of the `(` that opens it.
    ///
    /// The same reasoning as `viewBodyStart`, and a more ordinary trigger — a
    /// leading comment containing a parenthesis is enough.
    ///
    /// `None` for DDL with no column list at all, which includes
    /// `CREATE TABLE x AS SELECT ...`; that is skipped rather than mangled.
    let tableBodyStart (ddl: string) : int option =
        ddl |> scanFor (fun _ c depth -> c = '(' && depth = 0)

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
                            match viewBodyStart ddl with
                            | None -> None
                            | Some bodyStart ->
                                let body = ddl.Substring(bodyStart).TrimEnd().TrimEnd(';')

                                // The column-alias list and any options come
                                // with the name, because `pg_get_viewdef` folds
                                // the aliases into the query it renders.
                                exec (
                                    sprintf "CREATE VIEW %s %s AS %s" shadowName (viewNameSuffix ddl) body)

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
                            match tableBodyStart ddl with
                            | None -> None
                            | Some bodyStart ->
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

                            let bodyStart = tableBodyStart tableDdl |> Option.defaultValue -1

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
                                    text
                                    |> replaceSignificant
                                        [ sprintf "\"%s\".\"%s\"" schemaPart tablePart
                                          sprintf "\"%s\".%s" schemaPart tablePart
                                          sprintf "%s.\"%s\"" schemaPart tablePart
                                          sprintf "%s.%s" schemaPart tablePart ]
                                        target

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
