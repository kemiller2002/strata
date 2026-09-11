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
