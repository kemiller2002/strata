namespace Strata.Host.Postgres

open System
open Npgsql

/// Asks the server two things about deployed routines, and changes nothing.
///
/// Whether a redefinition can be applied with `CREATE OR REPLACE` is
/// PostgreSQL's rule, not one Strata can restate faithfully: a changed return
/// type, a renamed input parameter, a removed default and a function turned
/// procedure are each refused, and the declared and deployed renderings of a
/// signature differ in spelling even when they agree in meaning. So the
/// statement is tried, inside a transaction that is always rolled back, and
/// the server's own answer is used (strata#28).
///
/// What depends on a routine is read from `pg_depend`, so a drop is never
/// proposed for a routine a view, trigger, default or other routine still
/// needs.
module RoutineProbe =

    /// What a probe found.
    type Replacements =
        { /// Routines the server refused to replace in place, with its message.
          Rejected: (string * string) list
          /// Routines the probe could not decide, with why. They are planned
          /// as in-place replacements, and the apply reports any refusal.
          Undecided: (string * string) list }

    /// `invalid_function_definition` covers return type, parameter name and
    /// default changes; `wrong_object_type` a function becoming a procedure.
    let private refusals = set [ "42P13"; "42809" ]

    /// Try each `(key, CREATE OR REPLACE text)` in its own savepoint.
    ///
    /// Bodies are not checked (`check_function_bodies = off`): a body may name
    /// an object the same plan creates first, and what is being asked is
    /// whether the SIGNATURE can be replaced, not whether the body is sound.
    let replacements (connectionString: string) (candidates: (string * string) list) : Result<Replacements, string> =
        if List.isEmpty candidates then Ok { Rejected = []; Undecided = [] }
        else

        try
            use connection = new NpgsqlConnection(connectionString)
            connection.Open()
            use transaction = connection.BeginTransaction()

            let exec (sql: string) =
                use command = new NpgsqlCommand(sql, connection, transaction)
                command.ExecuteNonQuery() |> ignore

            let attempt (key: string, sql: string) =
                exec "SAVEPOINT strata_probe"

                try
                    exec sql
                    exec "ROLLBACK TO SAVEPOINT strata_probe; RELEASE SAVEPOINT strata_probe"
                    None
                with
                | :? PostgresException as ex ->
                    exec "ROLLBACK TO SAVEPOINT strata_probe; RELEASE SAVEPOINT strata_probe"
                    Some(key, Set.contains ex.SqlState refusals, ex.MessageText)

            try
                exec "SET LOCAL check_function_bodies = off"
                let outcomes = candidates |> List.choose attempt

                // ALWAYS. There is no success path that commits.
                transaction.Rollback()

                Ok
                    { Rejected = outcomes |> List.filter (fun (_, refused, _) -> refused) |> List.map (fun (k, _, m) -> k, m)
                      Undecided = outcomes |> List.filter (fun (_, refused, _) -> not refused) |> List.map (fun (k, _, m) -> k, m) }
            with ex ->
                try transaction.Rollback() with _ -> ()
                Error ex.Message
        with ex ->
            Error ex.Message

    /// The catalog objects depending on each `(key, signature)`, where the
    /// signature is `schema.name(argument types)` as `to_regprocedure` reads
    /// it. A signature the server cannot resolve is left out of the result,
    /// so the caller sees it as unread rather than as depended on by nothing.
    let dependents (connectionString: string) (candidates: (string * string) list) : Result<(string * string list) list, string> =
        if List.isEmpty candidates then Ok []
        else

        try
            use connection = new NpgsqlConnection(connectionString)
            connection.Open()

            use command =
                new NpgsqlCommand(
                    """
                    SELECT c.key,
                           COALESCE(array_agg(pg_catalog.pg_describe_object(d.classid, d.objid, d.objsubid))
                                    FILTER (WHERE d.objid IS NOT NULL), '{}')
                    FROM unnest(@keys::text[], @signatures::text[]) AS c(key, signature)
                    LEFT JOIN pg_catalog.pg_depend d
                           ON d.refclassid = 'pg_catalog.pg_proc'::regclass
                          AND d.refobjid = pg_catalog.to_regprocedure(c.signature)
                          AND d.deptype = 'n'
                    WHERE pg_catalog.to_regprocedure(c.signature) IS NOT NULL
                    GROUP BY c.key
                    """,
                    connection
                )

            command.Parameters.AddWithValue("keys", candidates |> List.map fst |> Array.ofList) |> ignore
            command.Parameters.AddWithValue("signatures", candidates |> List.map snd |> Array.ofList) |> ignore
            use reader = command.ExecuteReader()

            Ok(
                List.unfold
                    (fun () ->
                        if reader.Read() then Some((reader.GetString 0, reader.GetFieldValue<string[]> 1 |> List.ofArray |> List.sort), ())
                        else None)
                    ()
            )
        with ex ->
            Error ex.Message
