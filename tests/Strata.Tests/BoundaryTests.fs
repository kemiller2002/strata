/// The Aegis boundary of the command line, driven in process with a
/// deterministic collector sink, so each test can prove both that an
/// unexpected failure is recorded with the right code and that an ordinary
/// exit is not (DF-STRATA-FND-2026-0001).
module Strata.Tests.BoundaryTests

open System
open System.Data.Common
open System.IO
open System.Text.Json.Nodes
open Xunit
open Aegis
open Strata.Cli
open Strata.Host.Postgres

let private collector () =
    let sink = Sinks.Collector()
    sink, Boundary.configure [ sink.Sink() ]

let private recorded (sink: Sinks.Collector) =
    sink.Events |> List.map (fun event -> (JsonNode.Parse event).["code"].GetValue<string>())

/// What Npgsql throws is a DbException; the Cli cannot name Npgsql's type
/// (DF-STRATA-2026-E8C1), and the test does not need to either.
type private FakeDatabaseException(message: string) =
    inherit DbException(message)

let private failingWith (ex: exn) = fun () -> raise ex

[<Fact>]
let ``a command that completes keeps its own exit code and records nothing`` () =
    let sink, aegis = collector ()

    for code in [ 0; 1; 2 ] do
        Assert.Equal(Ok code, Boundary.capture aegis "Strata.Cli.Test" "could not test" (fun () -> code))

    // A refusal is an exit code the command chose, not a fault.
    Assert.Empty(sink.Events)

[<Fact>]
let ``the command line awaits delivery so the record cannot lose the race with exit`` () =
    let _, aegis = collector ()
    Assert.Equal(Blocking, aegis.Persistence)
    Assert.Equal("Strata", aegis.Application)

[<Fact>]
let ``a database failure that escapes the adapter is STRATA.POSTGRES.FAILURE`` () =
    let sink, aegis = collector ()

    match Boundary.capture aegis "Strata.Cli.Drift" "could not check the server for drift" (failingWith (FakeDatabaseException "28P01: password authentication failed")) with
    | Ok _ -> failwith "expected a fault"
    | Error fault ->
        Assert.Equal(Boundary.DatabaseFailure, fault.Code.Value)
        Assert.Equal(IntegrationFailure, fault.Category)
        Assert.StartsWith("Strata could not check the server for drift. PostgreSQL", fault.UserMessage)

    Assert.Equal<string list>([ Boundary.DatabaseFailure ], recorded sink)

[<Fact>]
let ``a filesystem failure that escapes the host is STRATA.FILES.FAILURE`` () =
    let sink, aegis = collector ()

    for ex in [ DirectoryNotFoundException "/nowhere/k.pem" :> exn; UnauthorizedAccessException "denied" :> exn ] do
        match Boundary.capture aegis "Strata.Cli.Keygen" "could not write the key pair" (failingWith ex) with
        | Ok _ -> failwith "expected a fault"
        | Error fault ->
            Assert.Equal(Boundary.FilesystemFailure, fault.Code.Value)
            Assert.Equal(InfrastructureFailure, fault.Category)

    Assert.Equal<string list>([ Boundary.FilesystemFailure; Boundary.FilesystemFailure ], recorded sink)

[<Fact>]
let ``anything else that escapes is STRATA.CLI.UNEXPECTED`` () =
    let sink, aegis = collector ()

    match Boundary.capture aegis "Strata.Cli" "could not complete the requested operation" (failingWith (Exception "boom")) with
    | Ok _ -> failwith "expected a fault"
    | Error fault -> Assert.Equal(Boundary.Unexpected, fault.Code.Value)

    Assert.Equal<string list>([ Boundary.Unexpected ], recorded sink)

[<Fact>]
let ``the operator sees a safe message and a reference, never the exception`` () =
    let _, aegis = collector ()
    let secret = "Password=hunter2 at /home/ops/.pgpass"

    match Boundary.capture aegis "Strata.Cli.Deploy" "could not complete the deployment" (failingWith (FakeDatabaseException secret)) with
    | Ok _ -> failwith "expected a fault"
    | Error fault ->
        let line = Boundary.describe fault
        Assert.StartsWith("error: Strata could not complete the deployment.", line)
        Assert.Matches(@"\[AG-[0-9A-Z]{5}\]$", line)
        Assert.DoesNotContain("hunter2", line)
        Assert.DoesNotContain(".pgpass", line)
        Assert.DoesNotContain("FakeDatabaseException", line)

[<Fact>]
let ``attempt turns a fault into the command's could-not-complete exit code`` () =
    let sink, aegis = collector ()
    Assert.Equal(2, Boundary.attempt aegis "Strata.Cli.Plan" "could not plan the deployment" 2 (failingWith (IOException "gone")))
    Assert.Equal(1, Boundary.attempt aegis "Strata.Cli.Query" "could not answer the query" 1 (failingWith (Exception "boom")))
    Assert.Equal(0, Boundary.attempt aegis "Strata.Cli.Query" "could not answer the query" 1 (fun () -> 0))
    Assert.Equal<string list>([ Boundary.FilesystemFailure; Boundary.Unexpected ], recorded sink)

[<Fact>]
let ``a programming defect fails loudly and is not disguised as an operational fault`` () =
    let sink, aegis = collector ()

    Assert.Throws<InvalidOperationException>(fun () ->
        Boundary.attempt aegis "Strata.Cli" "could not complete the requested operation" 2 (failingWith (InvalidOperationException "defect"))
        |> ignore)
    |> ignore

    Assert.Empty(sink.Events)

[<Fact>]
let ``cancellation is not a failure`` () =
    let sink, aegis = collector ()

    Assert.Throws<OperationCanceledException>(fun () ->
        Boundary.attempt aegis "Strata.Cli" "could not complete the requested operation" 2 (failingWith (OperationCanceledException()))
        |> ignore)
    |> ignore

    Assert.Empty(sink.Events)

[<Fact>]
let ``a malformed connection string is a typed refusal that never echoes the string`` () =
    for bad in [ "garbage"; "Host=localhost;Nonsense=1;Password=hunter2"; "Port=notanumber;Password=hunter2" ] do
        match ConnectionString.validate bad with
        | Ok() -> failwith $"accepted {bad}"
        | Error message -> Assert.DoesNotContain("hunter2", message)

    Assert.Equal(Ok(), ConnectionString.validate "Host=localhost;Port=5432;Database=x;Username=u;Password=p")
