namespace Strata.Host.Postgres

open System
open Npgsql

/// Whether the operator's connection string can be read at all.
///
/// A connection string Npgsql cannot parse is the operator's input, not an
/// operational failure, so it is answered here as a typed refusal before any
/// command opens a connection. Left to the adapters, it surfaces as an
/// `ArgumentException` from `new NpgsqlConnection`, which Aegis treats as a
/// programming defect and re-raises: an operator's typo would then end the
/// process with a stack trace (`DF-STRATA-FND-2026-0001`).
///
/// The refusal never echoes the string or Npgsql's message about it: a
/// connection string carries credentials.
module ConnectionString =

    let validate (connectionString: string) : Result<unit, string> =
        try
            NpgsqlConnectionStringBuilder(connectionString) |> ignore
            Ok()
        with
        | :? ArgumentException
        | :? FormatException
        | :? InvalidCastException
        | :? OverflowException ->
            Error "the connection string is not a valid PostgreSQL connection string; check its keywords and values."
