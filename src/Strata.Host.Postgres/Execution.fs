namespace Strata.Host.Postgres

open System
open Npgsql

/// Executing DDL against a live PostgreSQL database (PR-024).
///
/// Authority for: running statements and reporting exactly what ran.
///
/// This module holds NO policy. Whether a plan may run at all is decided by
/// the gate and by the caller before anything reaches here; `ER-019` says the
/// database remains the execution authority, and this is the thin layer that
/// respects that. What this module owes the caller is an accurate account of
/// what happened, especially when something failed partway.
module Execution =

    type StatementOutcome =
        | Executed of sql: string
        | Failed of sql: string * message: string
        /// Never attempted, because an earlier statement failed.
        | Skipped of sql: string

    type ExecutionResult =
        { Outcomes: StatementOutcome list
          /// True when every statement ran inside one transaction that
          /// committed. False means the database may hold some statements and
          /// not others, which the caller must report rather than summarise.
          Atomic: bool
          RolledBack: bool }

    [<RequireQualifiedAccess>]
    module ExecutionResult =

        let succeeded (result: ExecutionResult) =
            result.Outcomes
            |> List.forall (function
                | Executed _ -> true
                | Failed _
                | Skipped _ -> false)

    /// A fingerprint of the schema as it stands right now.
    ///
    /// Taken before a plan is applied and compared against the one taken when
    /// the plan was made. If they differ the database changed in between, and
    /// the plan was computed against a state that no longer exists — applying
    /// it would act on assumptions that have already been falsified.
    ///
    /// Reads only object and column identity: the plan is about structure, so
    /// a row insert must not invalidate it.
    let fingerprint (connectionString: string) : Result<string, string> =
        try
            use connection = new NpgsqlConnection(connectionString)
            connection.Open()

            use command =
                new NpgsqlCommand(
                    """
                    SELECT coalesce(md5(string_agg(entry, E'\n' ORDER BY entry)), '')
                    FROM (
                        -- relkind is "char", not text, and `text || "char"`
                        -- is an ambiguous operator. Cast it explicitly.
                        SELECT n.nspname || '.' || c.relname || ':' || c.relkind::text || ':' ||
                               coalesce(a.attname, '') || ':' ||
                               coalesce(format_type(a.atttypid, a.atttypmod), '') || ':' ||
                               coalesce(a.attnotnull::text, '') AS entry
                        FROM pg_class c
                        JOIN pg_namespace n ON n.oid = c.relnamespace
                        LEFT JOIN pg_attribute a
                               ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
                        WHERE c.relkind IN ('r', 'v', 'm', 'p')
                          AND n.nspname NOT IN ('pg_catalog', 'information_schema')
                          AND n.nspname NOT LIKE 'pg_toast%'
                          AND n.nspname NOT LIKE 'pg_temp%'
                    ) entries
                    """,
                    connection)

            match command.ExecuteScalar() with
            | :? string as value -> Ok value
            | _ -> Error "fingerprint query returned no value"
        with ex ->
            Error ex.Message

    /// Run every statement, in order, inside one transaction.
    ///
    /// One transaction because a half-applied schema change is the worst
    /// outcome available: the database then matches neither the desired state
    /// nor the state the plan was computed against, and the next plan is
    /// computed from that. PostgreSQL supports transactional DDL, so this is
    /// possible here in a way it is not on every engine — which is a reason to
    /// use it, not to design around its absence.
    ///
    /// Statements after a failure are reported as `Skipped`, never silently
    /// dropped: "9 of 12 ran" and "9 ran and 3 were never attempted" are the
    /// same fact stated with and without the part a reader needs.
    let apply (connectionString: string) (statements: string list) : ExecutionResult =
        if List.isEmpty statements then
            { Outcomes = []; Atomic = true; RolledBack = false }
        else
            use connection = new NpgsqlConnection(connectionString)
            connection.Open()
            use transaction = connection.BeginTransaction()

            let outcomes = ResizeArray<StatementOutcome>()
            let mutable failed = false

            for sql in statements do
                if failed then outcomes.Add(Skipped sql)
                else
                    try
                        use command = new NpgsqlCommand(sql, connection, transaction)
                        command.ExecuteNonQuery() |> ignore
                        outcomes.Add(Executed sql)
                    with ex ->
                        outcomes.Add(Failed(sql, ex.Message))
                        failed <- true

            if failed then
                transaction.Rollback()

                { Outcomes = List.ofSeq outcomes
                  Atomic = true
                  RolledBack = true }
            else
                transaction.Commit()

                { Outcomes = List.ofSeq outcomes
                  Atomic = true
                  RolledBack = false }
