/// Shares the live database with CatalogIntrospectionTests — see the note there.
[<Xunit.Collection("live-database")>]
module Strata.Tests.CliTests

open System
open System.Diagnostics
open System.IO
open Xunit
open Npgsql

/// Running `strata` as a process, and asserting on what it returns.
///
/// ## Why this exists
///
/// Several commands' contracts ARE their exit codes. `drift` answers 0 for a
/// match, 1 for a difference and 2 for could-not-tell; `compile` refuses a
/// project it could not read whole with 1; `deploy` refuses a version mismatch
/// with 2. Every one of those was verified by hand and written into a work-item
/// conclusion — evidence that does not re-run, and therefore evidence that stops
/// being true without anyone noticing.
///
/// The unit tests cover the functions underneath. What they cannot cover is the
/// wiring: a command dispatched on the wrong branch, a flag that no longer
/// reaches the function that reads it, an exit code lost on the way out of
/// `main`. That wiring has already been wrong once in this session — `add` took
/// `--project`'s VALUE as a path to add, because positional arguments were
/// "everything not starting with --".
module private Cli =

    let connectionString =
        match Environment.GetEnvironmentVariable "STRATA_TEST_PG" with
        | null | "" -> None
        | value -> Some value

    /// The binary this test assembly was built alongside.
    ///
    /// Located from the test assembly's own directory rather than by rebuilding:
    /// a test that invokes `dotnet build` is a test that can pass because it
    /// rebuilt something the rest of the suite did not use.
    let binary =
        let candidate =
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Strata.Cli", "bin")

        if not (Directory.Exists candidate) then
            None
        else
            Directory.GetFiles(candidate, "strata.dll", SearchOption.AllDirectories)
            |> Array.sortByDescending (fun p -> File.GetLastWriteTimeUtc p)
            |> Array.tryHead

    type Outcome =
        { ExitCode: int
          Stdout: string
          Stderr: string }

        member this.Output = this.Stdout + "\n" + this.Stderr

    let run (arguments: string list) : Outcome =
        let info = ProcessStartInfo("dotnet")
        info.ArgumentList.Add binary.Value

        for argument in arguments do
            info.ArgumentList.Add argument

        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.UseShellExecute <- false

        use proc = Process.Start info
        let stdout = proc.StandardOutput.ReadToEnd()
        let stderr = proc.StandardError.ReadToEnd()
        proc.WaitForExit()

        { ExitCode = proc.ExitCode; Stdout = stdout; Stderr = stderr }

type private RequiresCliAttribute() =
    inherit FactAttribute()
    do
        base.Skip <-
            match Cli.binary with
            | Some _ -> null
            | None -> "the strata binary was not found next to the test assembly; build the solution first"

type private RequiresCliAndPostgresAttribute() =
    inherit FactAttribute()
    do
        base.Skip <-
            match Cli.binary, Cli.connectionString with
            | Some _, Some _ -> null
            | None, _ -> "the strata binary was not found next to the test assembly; build the solution first"
            | _, None ->
                "STRATA_TEST_PG not set; live PostgreSQL integration test skipped. \
                 Build the fixture with scripts/test-fixture.sh and set the connection string it prints."

// ---- a project on disk ------------------------------------------------------

let private project (files: (string * string) list) (body: string -> unit) =
    let root = Path.Combine(Path.GetTempPath(), "strata-cli-" + Guid.NewGuid().ToString("N"))

    try
        // Created even with no files: a command that writes into the root must
        // find it there, and `keygen` used to ABORT rather than report when it
        // did not.
        Directory.CreateDirectory root |> ignore

        for relative, contents in files do
            let path = Path.Combine(root, relative)
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            File.WriteAllText(path, contents)

        body root
    finally
        try Directory.Delete(root, true) with _ -> ()

let private manifest (includes: string list) =
    "{\"include\":["
    + (includes |> List.map (fun i -> "\"" + i + "\"") |> String.concat ",")
    + "]}"

let private wellFormed =
    [ "strata.json", manifest [ "schema/cli/tables/thing.sql" ]
      "schema/cli/tables/thing.sql", "CREATE TABLE cli.thing (id bigint PRIMARY KEY, label text NOT NULL);" ]

/// A scratch database with the schema the project declares, and nothing in it.
let private database (body: string -> unit) =
    let name = "strata_cli_" + Guid.NewGuid().ToString("N").Substring(0, 10)
    let builder = NpgsqlConnectionStringBuilder(Cli.connectionString.Value)
    let admin = builder.ConnectionString

    let exec (connection: string) (sql: string) =
        use c = new NpgsqlConnection(connection)
        c.Open()
        use command = new NpgsqlCommand(sql, c)
        command.ExecuteNonQuery() |> ignore

    // A schema in the shared fixture database rather than a new database: the
    // test role deliberately is not a superuser, and three other tests depend on
    // that.
    exec admin (sprintf "DROP SCHEMA IF EXISTS cli CASCADE; CREATE SCHEMA cli")
    ignore name

    try
        body admin
    finally
        try exec admin "DROP SCHEMA IF EXISTS cli CASCADE" with _ -> ()

// ---- commands that need no database -----------------------------------------

[<RequiresCli>]
let ``help exits 0 and names every command`` () =
    // The help text is the only description of the tool most people read, so a
    // command that ships without a line here effectively did not ship.
    let outcome = Cli.run [ "--help" ]
    Assert.Equal(0, outcome.ExitCode)

    for command in
        [ "init"; "add"; "sync"; "compile"; "deploy"; "drift"; "validate"; "keygen"; "sign"; "plan"; "apply" ] do
        Assert.Contains(command, outcome.Stdout)

[<RequiresCli>]
let ``a command that needs a connection and has none exits 2`` () =
    let outcome = Cli.run [ "plan"; "--project"; "." ]
    Assert.Equal(2, outcome.ExitCode)
    Assert.Contains("connection", outcome.Output)

[<RequiresCli>]
let ``init writes a manifest and exits 0`` () =
    project
        [ "schema/cli/tables/thing.sql", "CREATE TABLE cli.thing (id bigint PRIMARY KEY);" ]
        (fun root ->
            let outcome = Cli.run [ "init"; "--project"; root ]
            Assert.Equal(0, outcome.ExitCode)
            Assert.True(File.Exists(Path.Combine(root, "strata.json")))
            Assert.Contains("schema/cli/tables/thing.sql", File.ReadAllText(Path.Combine(root, "strata.json"))))

[<RequiresCli>]
let ``init refuses to overwrite an existing manifest`` () =
    project wellFormed (fun root ->
        let outcome = Cli.run [ "init"; "--project"; root ]
        Assert.Equal(2, outcome.ExitCode)
        Assert.Contains("already exists", outcome.Output))

[<RequiresCli>]
let ``add takes the path and not the value of --project`` () =
    // The wiring defect this file exists for. Positional arguments were
    // "everything not starting with --", so the directory after `--project`
    // survived the filter and `add` tried to add it as an object file.
    project wellFormed (fun root ->
        File.WriteAllText(
            Path.Combine(root, "schema/cli/tables/second.sql"),
            "CREATE TABLE cli.second (id bigint PRIMARY KEY);")

        let outcome = Cli.run [ "add"; "schema/cli/tables/second.sql"; "--project"; root ]
        Assert.Equal(0, outcome.ExitCode)

        let written = File.ReadAllText(Path.Combine(root, "strata.json"))
        Assert.Contains("schema/cli/tables/second.sql", written)
        // The directory must not have been written as an object file.
        Assert.DoesNotContain(root, written))

[<RequiresCli>]
let ``sync shows what it would do and writes nothing without --confirm`` () =
    project wellFormed (fun root ->
        File.WriteAllText(
            Path.Combine(root, "schema/cli/tables/dropped_in.sql"),
            "CREATE TABLE cli.dropped_in (id bigint PRIMARY KEY);")

        let before = File.ReadAllText(Path.Combine(root, "strata.json"))
        let outcome = Cli.run [ "sync"; "--project"; root ]

        Assert.Equal(2, outcome.ExitCode)
        Assert.Contains("dropped_in.sql", outcome.Stdout)
        Assert.Equal(before, File.ReadAllText(Path.Combine(root, "strata.json")))

        let confirmed = Cli.run [ "sync"; "--project"; root; "--confirm" ]
        Assert.Equal(0, confirmed.ExitCode)
        Assert.Contains("dropped_in.sql", File.ReadAllText(Path.Combine(root, "strata.json"))))

[<RequiresCli>]
let ``keygen writes a key pair and needs no connection`` () =
    project [] (fun root ->
        let privatePath = Path.Combine(root, "k.pem")
        let publicPath = Path.Combine(root, "k.pub")
        let outcome = Cli.run [ "keygen"; "--key"; privatePath; "--public-key"; publicPath ]

        Assert.Equal(0, outcome.ExitCode)
        Assert.Contains("PRIVATE KEY", File.ReadAllText privatePath)
        Assert.Contains("PUBLIC KEY", File.ReadAllText publicPath)
        // The guarantee depends entirely on who can read the key, so the command
        // has to say so rather than let an operator infer otherwise.
        Assert.Contains("forge", outcome.Stdout))

// ---- the compile / deploy / drift chain -------------------------------------

[<RequiresCliAndPostgres>]
let ``compile writes an artifact and exits 0`` () =
    database (fun connection ->
        project wellFormed (fun root ->
            let artifact = Path.Combine(root, "out.strata")
            let outcome = Cli.run [ "compile"; "--project"; root; "--connection"; connection; "--out"; artifact ]

            Assert.Equal(0, outcome.ExitCode)
            Assert.True(File.Exists artifact)))

[<RequiresCliAndPostgres>]
let ``compile refuses a project it cannot read whole, and writes nothing`` () =
    // The asymmetry with `plan`, as a contract rather than a paragraph: an
    // artifact is a CLAIM that the project was read whole.
    database (fun connection ->
        project
            [ "strata.json", manifest [ "schema/cli/tables/broken.sql" ]
              "schema/cli/tables/broken.sql", "CREATE TABL cli.broken (id bigint);" ]
            (fun root ->
                let artifact = Path.Combine(root, "out.strata")
                let outcome = Cli.run [ "compile"; "--project"; root; "--connection"; connection; "--out"; artifact ]

                Assert.Equal(1, outcome.ExitCode)
                Assert.False(File.Exists artifact)
                Assert.Contains("broken.sql", outcome.Output)))

[<RequiresCliAndPostgres>]
let ``the same project compiles to the same bytes twice`` () =
    // NFR-001, and the precondition for signing an artifact at all.
    database (fun connection ->
        project wellFormed (fun root ->
            let first = Path.Combine(root, "first.strata")
            let second = Path.Combine(root, "second.strata")

            Assert.Equal(0, (Cli.run [ "compile"; "--project"; root; "--connection"; connection; "--out"; first ]).ExitCode)
            Assert.Equal(0, (Cli.run [ "compile"; "--project"; root; "--connection"; connection; "--out"; second ]).ExitCode)
            Assert.Equal(File.ReadAllText first, File.ReadAllText second)))

[<RequiresCliAndPostgres>]
let ``deploy applies an artifact with the project deleted, and drift then reports a match`` () =
    // The guarantee, as a test rather than a paragraph: deploy reads the
    // artifact and nothing else.
    database (fun connection ->
        let artifact = Path.Combine(Path.GetTempPath(), "strata-cli-" + Guid.NewGuid().ToString("N") + ".strata")

        try
            project wellFormed (fun root ->
                Assert.Equal(
                    0,
                    (Cli.run [ "compile"; "--project"; root; "--connection"; connection; "--out"; artifact ]).ExitCode))

            // The project directory is gone by here: `project` deleted it.
            let deployed =
                Cli.run [ "deploy"; "--artifact"; artifact; "--connection"; connection; "--confirm" ]

            Assert.Equal(0, deployed.ExitCode)

            let drifted = Cli.run [ "drift"; "--artifact"; artifact; "--connection"; connection ]
            Assert.Equal(0, drifted.ExitCode)
            Assert.Contains("NO DRIFT", drifted.Stdout)
        finally
            try File.Delete artifact with _ -> ())

[<RequiresCliAndPostgres>]
let ``drift exits 1 when the target has an object the artifact does not`` () =
    // Removals count as drift. Without that this reports a match on a server
    // carrying an extra table, which is the case monitoring exists for.
    database (fun connection ->
        let artifact = Path.Combine(Path.GetTempPath(), "strata-cli-" + Guid.NewGuid().ToString("N") + ".strata")

        try
            project wellFormed (fun root ->
                Assert.Equal(
                    0,
                    (Cli.run [ "compile"; "--project"; root; "--connection"; connection; "--out"; artifact ]).ExitCode)

                Assert.Equal(
                    0,
                    (Cli.run [ "deploy"; "--artifact"; artifact; "--connection"; connection; "--confirm" ]).ExitCode))

            use c = new NpgsqlConnection(connection)
            c.Open()
            use command = new NpgsqlCommand("CREATE TABLE cli.rogue (id bigint PRIMARY KEY)", c)
            command.ExecuteNonQuery() |> ignore

            let drifted = Cli.run [ "drift"; "--artifact"; artifact; "--connection"; connection ]
            Assert.Equal(1, drifted.ExitCode)
            Assert.Contains("DRIFT", drifted.Stdout)
        finally
            try File.Delete artifact with _ -> ())

[<RequiresCliAndPostgres>]
let ``deploy refuses an unsigned artifact when a signature is required`` () =
    database (fun connection ->
        project wellFormed (fun root ->
            let artifact = Path.Combine(root, "out.strata")
            let publicPath = Path.Combine(root, "k.pub")
            let privatePath = Path.Combine(root, "k.pem")

            Assert.Equal(0, (Cli.run [ "keygen"; "--key"; privatePath; "--public-key"; publicPath ]).ExitCode)

            Assert.Equal(
                0,
                (Cli.run [ "compile"; "--project"; root; "--connection"; connection; "--out"; artifact ]).ExitCode)

            let refused =
                Cli.run
                    [ "deploy"; "--artifact"; artifact; "--connection"; connection
                      "--require-signature"; "--public-key"; publicPath ]

            Assert.Equal(2, refused.ExitCode)
            Assert.Contains("not signed", refused.Output)

            Assert.Equal(0, (Cli.run [ "sign"; "--artifact"; artifact; "--key"; privatePath ]).ExitCode)

            let accepted =
                Cli.run
                    [ "deploy"; "--artifact"; artifact; "--connection"; connection
                      "--require-signature"; "--public-key"; publicPath; "--confirm" ]

            Assert.Equal(0, accepted.ExitCode)
            Assert.Contains("Signature verified", accepted.Stdout)))

[<RequiresCliAndPostgres>]
let ``deploy refuses an artifact edited after it was compiled`` () =
    database (fun connection ->
        project wellFormed (fun root ->
            let artifact = Path.Combine(root, "out.strata")

            Assert.Equal(
                0,
                (Cli.run [ "compile"; "--project"; root; "--connection"; connection; "--out"; artifact ]).ExitCode)

            File.WriteAllText(artifact, (File.ReadAllText artifact).Replace("bigint", "integer"))

            let outcome = Cli.run [ "deploy"; "--artifact"; artifact; "--connection"; connection ]
            Assert.Equal(2, outcome.ExitCode)
            Assert.Contains("integrity digest", outcome.Output)))

// ---- validating without a connection ----------------------------------------

[<RequiresCliAndPostgres>]
let ``validate against an artifact needs no connection`` () =
    // The headline of WI-0085: an editor, a pre-commit hook and a fork's pull
    // request have no credentials, and those are where the check is worth most.
    database (fun connection ->
        project wellFormed (fun root ->
            let artifact = Path.Combine(root, "out.strata")

            Assert.Equal(
                0,
                (Cli.run [ "compile"; "--project"; root; "--connection"; connection; "--out"; artifact ]).ExitCode)

            let good = Path.Combine(root, "good.sql")
            File.WriteAllText(good, "SELECT id, label FROM cli.thing;")

            let outcome = Cli.run [ "validate"; good; "--artifact"; artifact ]
            Assert.Equal(0, outcome.ExitCode)
            Assert.Contains("VALID", outcome.Stdout)

            let bad = Path.Combine(root, "bad.sql")
            File.WriteAllText(bad, "SELECT id, lable FROM cli.thing;")

            let refused = Cli.run [ "validate"; bad; "--artifact"; artifact ]
            Assert.NotEqual(0, refused.ExitCode)))

[<RequiresCli>]
let ``keygen reports a path it cannot write rather than aborting`` () =
    // Found by this harness on its first run: the command exited 134 — SIGABRT,
    // an unhandled exception — because the directory did not exist. A tool that
    // aborts instead of saying what went wrong teaches people to distrust its
    // exit codes, which is the opposite of what a test suite about exit codes is
    // for.
    let missing = Path.Combine(Path.GetTempPath(), "strata-cli-" + Guid.NewGuid().ToString("N"), "nested", "k.pem")
    let outcome = Cli.run [ "keygen"; "--key"; missing; "--public-key"; missing + ".pub" ]

    Assert.Equal(2, outcome.ExitCode)
    Assert.Contains("could not write", outcome.Stderr)
