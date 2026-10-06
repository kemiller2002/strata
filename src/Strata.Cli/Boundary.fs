/// The Aegis boundary of the Strata command line.
///
/// Authority for: how an *unexpected* operational failure ends a Strata
/// command. Expected outcomes are not faults: a refused deployment, a drift, a
/// file the project does not list, a connection string that does not parse —
/// those stay typed results with their own messages and exit codes. What
/// reaches here is what no adapter anticipated: a PostgreSQL error the host
/// did not turn into a result, a file that vanished between check and read,
/// anything else that escaped.
///
/// Each such failure is classified once, here, into a stable code declared in
/// `aegis-boundaries.json`, recorded through Aegis, and shown to the operator
/// as a safe message and a reference. The raw exception text is never printed
/// as the error line; it is in the fault record Aegis writes to standard
/// error, after Aegis's redaction rules.
///
/// Programming defects and cancellation keep Aegis's semantics: they are
/// re-raised, not disguised as an operational failure.
///
/// Delivery is awaited (`Blocking`): a command-line process may exit as soon as
/// the command returns, and a detached delivery would race that exit.
module Strata.Cli.Boundary

open System
open System.Data.Common
open System.IO
open Aegis

/// The PostgreSQL catalog and deployment adapter failed in a way it did not
/// translate into a result.
[<Literal>]
let DatabaseFailure = "STRATA.POSTGRES.FAILURE"

/// The filesystem corpus, project or artifact adapter failed in a way it did
/// not translate into a result.
[<Literal>]
let FilesystemFailure = "STRATA.FILES.FAILURE"

/// Anything else that escaped a command.
[<Literal>]
let Unexpected = "STRATA.CLI.UNEXPECTED"

/// Every code this boundary can record. `aegis-boundaries.json` must declare
/// exactly these (FoundationsConformanceTests).
let codes = [ DatabaseFailure; FilesystemFailure; Unexpected ]

/// Aegis configured once for the process and validated before it is trusted.
let configure (sinks: Sinks.Sink list) : AegisConfig =
    let configured =
        { Aegis.configure "Strata" None sinks with
            Persistence = Blocking
            Fallback = fun message -> Console.Error.WriteLine message }

    match Bootstrap.validate None configured with
    | Ok valid -> valid
    | Result.Error problems -> invalidOp $"Invalid Strata Aegis configuration: %A{problems}"

/// One central translation from an escaped exception to a fault. `failure`
/// is the caller's safe description of what did not happen ("could not write
/// the key pair"); the classification adds what kind of boundary failed.
///
/// `DbException` rather than Npgsql's own type: only `Strata.Host.Postgres`
/// references Npgsql (`DF-STRATA-2026-E8C1`), and every Npgsql exception
/// derives from it.
let classify (failure: string) (aegis: AegisConfig) (scope: Scope) (ex: exn) : Fault =
    let code, category, hint =
        match ex with
        | :? DbException ->
            DatabaseFailure,
            IntegrationFailure,
            "PostgreSQL reported an error. Check that the server is reachable and accepts the connection's credentials."
        | :? IOException
        | :? UnauthorizedAccessException ->
            FilesystemFailure,
            InfrastructureFailure,
            "A file or directory could not be read or written. Check that the path exists and is accessible."
        | _ -> Unexpected, IntegrationFailure, "The failure was not one Strata anticipated."

    Aegis.faultOf
        aegis
        scope
        (FaultCode code)
        category
        FaultSeverity.Error
        OperationOnly
        Transient
        Continue
        $"Strata {failure}. {hint}"
        ex

/// Run one command step at the boundary: its own exit code, or the recorded
/// fault. Programming defects and cancellation are re-raised by Aegis.
let capture (aegis: AegisConfig) (operation: string) (failure: string) (step: unit -> int) : Result<int, Fault> =
    let scope = Aegis.scope aegis operation Map.empty
    Aegis.capture aegis scope (classify failure aegis) step

/// The error line an operator sees: the safe message and a reference that
/// finds the full fault record. Never the exception.
let describe (fault: Fault) =
    $"error: {fault.UserMessage} [{Presentation.reference fault}]"

/// `capture`, with the fault printed and turned into the command's
/// could-not-complete exit code.
let attempt (aegis: AegisConfig) (operation: string) (failure: string) (exitCode: int) (step: unit -> int) : int =
    match capture aegis operation failure step with
    | Ok code -> code
    | Result.Error fault ->
        eprintfn "%s" (describe fault)
        exitCode
