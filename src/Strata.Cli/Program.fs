module Strata.Cli.Program

open System
open System.IO
open Strata.Semantic.Identity
open Strata.Semantic.AnalysisScope
open Strata.Analysis.Graph
open Strata.Analysis.Corpus
open Strata.Application
open Strata.Host.Postgres
open Strata.Host.PgParser
open Strata.Host.Files

/// Composition root. Holds no semantic decisions — it parses arguments, calls
/// the adapters, and prints what the application tier returns.

let private usage =
    """strata — semantic infrastructure for relational databases

USAGE
  strata <command> [args] --connection <connection-string> [--json]

COMMANDS
  plan [--project <dir>]           Dry run: diff the project's desired state
                                   against the live database. Exit 0 allow,
                                   1 block, 2 requires approval.
  apply [--project <dir>]          Execute the plan. Requires --confirm, and
                                   refuses unless the gate allows every change.
  validate <file.sql>              Check every relation and column in a SQL file
                                   against the live schema. Exit 0 valid,
                                   1 a reference provably does not exist,
                                   2 something could not be verified.
  check <proposed.sql>             Gate a proposed migration. Exit 0 allow,
                                   1 block, 2 requires approval.
  scope                            Print the analysis scope alone, once
  inspect <schema.object>          Describe an object
  relationships <schema.object>    Relationships touching an object
  path <schema.a> <schema.b>       Relationship path between two objects
  impact <schema.table.column>     What breaks if a column is dropped/changed
  readers <schema.object>          Sources that read an object
  writers <schema.object>          Sources that write an object

OPTIONS
  --connection <s>   PostgreSQL connection string (or set STRATA_PG)
  --corpus <dir>     Directory of .sql files to index (or set STRATA_CORPUS)
  --project <dir>    Project root: a directory of object files (default: .)
  --confirm          Required by `apply`. Without it, apply dry-runs and stops.
  --allow-drops      Propose removals. Without it, objects present in the
                     database and absent from the project are REPORTED but
                     never proposed for dropping.
  --json             Machine-readable output (default is human-readable)
  --brief            With --json, replace the scope block with a digest. Fetch
                     the full scope once via `strata scope`. Caveats that change
                     how a result must be read are ALWAYS kept inline.

NOTES
  Every answer carries its analysis scope. A result is bounded by what was
  actually inspected; "no readers" is not the same claim as "nothing reads it".

  Without --corpus, no SQL is indexed, so readers/writers are necessarily
  empty and every answer says so.

  `validate` distinguishes "does not exist" from "could not be verified" and
  never merges them. An incomplete catalog snapshot yields exit 2, not exit 1:
  Strata does not claim absence it cannot support, because an author who
  "fixes" working SQL to satisfy a false error is worse off than with no
  validator at all.

  For a multi-query session: run `strata scope` once, then pass --brief on
  each answer. The scope is identical across queries against one snapshot, so
  repeating it costs more than the answers themselves.
"""

/// Parse `schema.object`. An unqualified name is accepted and stays
/// unqualified — resolution against search_path is the analysis tier's job,
/// not the CLI's (RK-006).
let private parseName (text: string) =
    match text.Split('.') with
    | [| schema; name |] -> QualifiedName.qualified (Identifier.unquoted schema) (Identifier.unquoted name)
    | _ -> QualifiedName.unqualified (Identifier.unquoted text)

let private valueOf (flag: string) (argv: string list) =
    argv
    |> List.pairwise
    |> List.tryPick (fun (a, b) -> if a = flag then Some b else None)

/// A Debug binary is roughly 1.6x slower than a Release one on the extraction
/// path. Two published timings were taken from a Debug build without anyone
/// noticing, because nothing in the invocation or the output said so. Saying
/// it here, on stderr, means a timing cannot be recorded silently again;
/// stderr keeps it out of `--json` output, which stays byte-identical.
let private announceBuildConfiguration () =
#if DEBUG
    eprintfn "strata: DEBUG build - timings from this binary are not representative of Release"
#else
    ()
#endif

[<EntryPoint>]
let main argv =
    announceBuildConfiguration ()
    let args = List.ofArray argv

    if List.isEmpty args || List.contains "--help" args || List.contains "-h" args then
        printfn "%s" usage
        0
    else

    let connection =
        match valueOf "--connection" args with
        | Some c -> Some c
        | None ->
            match Environment.GetEnvironmentVariable "STRATA_PG" with
            | null | "" -> None
            | value -> Some value

    match connection with
    | None ->
        eprintfn "error: no connection string. Pass --connection or set STRATA_PG."
        2
    | Some connectionString ->

    let positional = args |> List.filter (fun a -> not (a.StartsWith "--")) 

    // `check` is not a retrieval query — it reads a file and returns an exit
    // code — so it is dispatched before the query parser.
    let corpusDirectory =
        match valueOf "--corpus" args with
        | Some dir -> Some dir
        | None ->
            match Environment.GetEnvironmentVariable "STRATA_CORPUS" with
            | null | "" -> None
            | value -> Some value

    let gateFile =
        match positional with
        | "check" :: path :: _ -> Some path
        | _ -> None

    let validateFile =
        match positional with
        | "validate" :: path :: _ -> Some path
        | _ -> None

    let projectRoot =
        match valueOf "--project" args with
        | Some dir -> dir
        | None -> "."

    let wantsPlan =
        match positional with
        | "plan" :: _
        | "apply" :: _ -> true
        | _ -> false

    let wantsApply =
        match positional with
        | "apply" :: _ -> true
        | _ -> false

    let query =
        match positional with
        | "scope" :: _ -> Ok Retrieval.ScopeOnly
        | "inspect" :: name :: _ -> Ok(Retrieval.Inspect(parseName name))
        | "relationships" :: name :: _ -> Ok(Retrieval.Relationships(parseName name))
        | "path" :: a :: b :: _ -> Ok(Retrieval.Path(parseName a, parseName b))
        | "impact" :: target :: _ ->
            // schema.table.column — the last segment is the column.
            match target.Split('.') with
            | [| schema; table; column |] ->
                Ok(
                    Retrieval.ColumnImpact(
                        QualifiedName.qualified (Identifier.unquoted schema) (Identifier.unquoted table),
                        Identifier.unquoted column
                    )
                )
            | _ -> Error "impact needs a fully qualified schema.table.column"
        | "readers" :: name :: _ -> Ok(Retrieval.Readers(parseName name))
        | "writers" :: name :: _ -> Ok(Retrieval.Writers(parseName name))
        | "validate" :: _ -> Error "validate needs a path to a .sql file"
        | "plan" :: _ -> Error "plan takes no positional arguments; use --project"
        | "apply" :: _ -> Error "apply takes no positional arguments; use --project"
        | command :: _ -> Error(sprintf "unknown or incomplete command: %s" command)
        | [] -> Error "no command given"

    if wantsPlan then
        try
            match Project.read projectRoot with
            | Error message ->
                eprintfn "error: %s" message
                2
            | Ok project ->
                let parser = PgParserAdapter.PostgresParser() :> Strata.Analysis.DialectPort.IDialectParser

                let declared =
                    DesiredState.load
                        parser
                        (project.Files |> List.map (fun f -> f.Path, f.Contents))

                // A file that could not even be located or read never reached
                // the loader, so its failure has to be folded in here or the
                // desired state would call itself complete while missing it.
                let desired =
                    if List.isEmpty project.Failures then declared.Snapshot
                    else
                        { declared.Snapshot with
                            Completeness =
                                Completeness.ofList
                                    (declared.Snapshot.Completeness.Categories
                                     |> List.map (fun (name, state) ->
                                         if name = "relations" then
                                             name,
                                             Partial(
                                                 sprintf
                                                     "%d project file(s) could not be read"
                                                     (List.length project.Failures))
                                         else
                                             name, state)) }

                for path, reason in project.Failures do
                    eprintfn "warning: %s: %s" path reason

                for failure in declared.Failures do
                    eprintfn "warning: %s: %s" failure.Path failure.Reason

                // Taken BEFORE introspection so it covers the whole window in
                // which the plan is computed, not just the tail of it.
                let fingerprintBeforePlanning =
                    match Execution.fingerprint connectionString with
                    | Ok value -> value
                    | Microsoft.FSharp.Core.Error _ -> ""

                let actual = CatalogIntrospection.introspect connectionString

                let searchPath =
                    match CatalogIntrospection.readSearchPath connectionString with
                    | Ok p -> p
                    | Error _ -> []

                // Where the application SQL lives is the one thing the
                // directory tree cannot say, so it comes from --corpus or from
                // the manifest. Without it the gate can still run, but it can
                // only ever answer requires-approval on a removal: "no
                // dependency found" is not "no dependency exists" when nothing
                // was searched.
                let corpusRoots =
                    match corpusDirectory with
                    | Some dir -> [ dir ]
                    | None ->
                        project.Manifest.CorpusRoots
                        |> List.map (fun root -> IO.Path.Combine(projectRoot, root))

                if List.isEmpty corpusRoots then
                    eprintfn "warning: no SQL corpus given (--corpus or corpusRoots in strata.json)."
                    eprintfn "         Removals cannot be cleared, only approved: with nothing indexed,"
                    eprintfn "         \"no dependency found\" is not evidence that none exists."

                let corpusSources =
                    corpusRoots
                    |> List.collect (fun root ->
                        match FileCorpus.read root with
                        | Ok r -> r.Sources
                        | Error _ -> [])

                let analysis = CorpusPipeline.analyse parser actual searchPath corpusSources
                let graph = CorpusPipeline.buildGraph actual analysis
                let scope = CorpusPipeline.toScope parser actual analysis

                let allowDrops = List.contains "--allow-drops" args
                let diff = SchemaDiff.run allowDrops project.Manifest.ManagedSchemas declared.Declarations desired actual
                let gate = DeploymentGate.run graph scope diff.Changes

                if List.contains "--json" args then
                    printfn "%s" (SchemaDiff.toJson diff gate)
                else
                    printfn "%s" (SchemaDiff.toText diff gate)

                // An empty change list means two different things depending on
                // where it came from, and the gate cannot tell them apart.
                //
                // From a parsed migration script it means "Strata recognised
                // nothing in what you gave it", which the gate rightly treats
                // as requires-approval. From a DIFF it means the database
                // already matches desired state — convergence, which is the
                // success case a declarative tool exists to reach. Reporting
                // the second as requires-approval would make every idempotent
                // re-run look like a problem.
                let converged = List.isEmpty diff.Changes

                if converged then
                    printfn ""
                    printfn "Database already matches desired state; nothing to apply."

                if not wantsApply then
                    if converged then 0 else DeploymentGate.Verdict.exitCode gate.Verdict
                elif converged then
                    0
                else

                // Everything below is the only irreversible thing Strata does,
                // so each refusal is separate and each says which one fired.
                let unwritable =
                    diff.Statements |> List.filter (fun s -> s.Sql.IsNone)

                if gate.Verdict <> DeploymentGate.Allow then
                    eprintfn ""
                    eprintfn "REFUSED: the gate did not allow this plan (%s)." (DeploymentGate.Verdict.tag gate.Verdict)
                    eprintfn "         Nothing was executed. Address the findings above and re-run."
                    DeploymentGate.Verdict.exitCode gate.Verdict

                elif not (List.isEmpty unwritable) then
                    // Running the rest would leave the database matching neither
                    // the desired state nor the state the plan was computed from.
                    eprintfn ""
                    eprintfn "REFUSED: %d change(s) were classified but cannot be written as DDL:" (List.length unwritable)

                    for s in unwritable do
                        eprintfn "         - %s" (Strata.Analysis.ProposedChange.Change.tag s.Change)

                    eprintfn "         Applying the remainder would leave the database matching neither side."
                    2

                elif not (List.contains "--confirm" args) then
                    printfn ""
                    printfn "Dry run only. Re-run with --confirm to execute these %d statement(s)." (List.length diff.Changes)
                    2

                else

                // The plan was computed against a snapshot. If the database has
                // moved since, the plan's assumptions are already falsified —
                // so it is re-fingerprinted immediately before executing and
                // compared with the value taken before planning.
                match Execution.fingerprint connectionString with
                | Microsoft.FSharp.Core.Error message ->
                    eprintfn "REFUSED: could not fingerprint the database before applying: %s" message
                    2
                | Ok afterPlanning when afterPlanning <> fingerprintBeforePlanning ->
                    eprintfn ""
                    eprintfn "REFUSED: the database schema changed while this plan was being computed."
                    eprintfn "         The plan was built against a state that no longer exists. Re-run."
                    2
                | Ok _ ->
                    let statements = diff.Statements |> List.choose (fun s -> s.Sql)
                    let result = Execution.apply connectionString statements

                    printfn ""

                    for outcome in result.Outcomes do
                        match outcome with
                        | Execution.Executed sql -> printfn "  ok      %s" (sql.Replace("\n", " "))
                        | Execution.Failed (sql, message) ->
                            printfn "  FAILED  %s" (sql.Replace("\n", " "))
                            printfn "          %s" message
                        | Execution.Skipped sql ->
                            printfn "  skipped %s" (sql.Replace("\n", " "))

                    printfn ""

                    if result.RolledBack then
                        printfn "ROLLED BACK. The database is unchanged; no statement took effect."
                        1
                    else
                        printfn "Applied %d statement(s)." (List.length statements)
                        0
        with ex ->
            eprintfn "error: %s" ex.Message
            2
    else

    match validateFile with
    | Some path when not (IO.File.Exists path) ->
        eprintfn "error: file not found: %s" path
        2
    | Some path ->
        try
            let snapshot = CatalogIntrospection.introspect connectionString

            let searchPath =
                match CatalogIntrospection.readSearchPath connectionString with
                | Ok p -> p
                | Error _ -> []

            let parser = PgParserAdapter.PostgresParser() :> Strata.Analysis.DialectPort.IDialectParser
            let report = Validation.validate parser snapshot searchPath (IO.File.ReadAllText path)

            if List.contains "--json" args then
                printfn "%s" (Validation.toJson path report)
            else
                printfn "%s" (Validation.toText path report)

            Validation.Report.exitCode report
        with ex ->
            eprintfn "error: %s" ex.Message
            2

    | None ->

    match gateFile with
    | Some path when not (IO.File.Exists path) ->
        eprintfn "error: proposed migration not found: %s" path
        2
    | Some path ->
        try
            let snapshot = CatalogIntrospection.introspect connectionString

            let searchPath =
                match CatalogIntrospection.readSearchPath connectionString with
                | Ok p -> p
                | Error _ -> []

            let parser = PgParserAdapter.PostgresParser() :> Strata.Analysis.DialectPort.IDialectParser

            // The gate needs the corpus: without it there are no dependencies,
            // and the scope will correctly refuse to support an absence claim.
            let corpusSources =
                match corpusDirectory with
                | None -> []
                | Some directory ->
                    match FileCorpus.read directory with
                    | Ok r -> r.Sources
                    | Error _ -> []

            let analysis = CorpusPipeline.analyse parser snapshot searchPath corpusSources
            let graph = CorpusPipeline.buildGraph snapshot analysis
            let scope = CorpusPipeline.toScope parser snapshot analysis

            let proposedText = IO.File.ReadAllText path

            let changes =
                parser.ParseScript proposedText
                |> List.collect (fun parsed ->
                    match parsed with
                    | Strata.Analysis.DialectPort.Failed (_, error) ->
                        // A migration statement that does not parse is not a
                        // migration statement with no effect.
                        [ Strata.Analysis.ProposedChange.UnclassifiedChange(sprintf "unparseable: %s" error.Message) ]
                    | Strata.Analysis.DialectPort.Parsed (location, extraction) ->
                        let statementText =
                            if location.Length > 0 && location.Offset + location.Length <= proposedText.Length then
                                proposedText.Substring(location.Offset, location.Length)
                            else
                                proposedText

                        Strata.Analysis.ProposedChange.ofStatement extraction
                        |> List.map (Strata.Analysis.ProposedChange.refineWithText statementText))

            let result = DeploymentGate.run graph scope changes

            if List.contains "--json" args then
                printfn "%s" (DeploymentGate.toJson result)
            else
                printfn "%s" (DeploymentGate.toText result)

            DeploymentGate.Verdict.exitCode result.Verdict
        with ex ->
            eprintfn "error: %s" ex.Message
            2

    | None ->

    match query with
    | Error message ->
        eprintfn "error: %s" message
        eprintfn ""
        eprintfn "%s" usage
        2
    | Ok query ->

    try
        let snapshot = CatalogIntrospection.introspect connectionString

        let searchPath =
            match CatalogIntrospection.readSearchPath connectionString with
            | Ok path -> path
            | Error _ ->
                // An unreadable search_path is not an empty one. Leaving it
                // empty keeps unqualified names PartiallyResolved rather than
                // resolving them against a guess (RK-006).
                []

        let graph, scope, readFailures =
            match corpusDirectory with
            | None ->
                // No corpus indexed: declared relationships only, and no
                // read/write dependencies. The scope says so rather than
                // leaving the emptiness to be misread (§144.11).
                { SemanticGraph.empty with
                    Relationships = SemanticGraph.declaredFrom snapshot
                    Dependencies = [] },
                { Scope.nothingAnalyzed with
                    LiveDatabaseInspected = true
                    SchemaCompleteness = snapshot.Completeness
                    Corpus = CorpusScope.empty
                    // No SQL was parsed, so grammar compatibility does not
                    // arise. That is a different statement from "the grammars
                    // match", and the type keeps them apart.
                    DialectCompatibility = NoParsingPerformed },
                []

            | Some directory ->
                match FileCorpus.read directory with
                | Error message ->
                    eprintfn "error: %s" message
                    exit 1
                | Ok corpusRead ->
                    let parser = PgParserAdapter.PostgresParser() :> Strata.Analysis.DialectPort.IDialectParser

                    let analysis =
                        CorpusPipeline.analyse parser snapshot searchPath corpusRead.Sources

                    CorpusPipeline.buildGraph snapshot analysis,
                    CorpusPipeline.toScope parser snapshot analysis,
                    corpusRead.Failures

        let answer = Retrieval.answer snapshot graph scope query

        // Files Strata could not read are reported before the answer, because
        // they bound it and a reader must not miss them.
        for failure in readFailures do
            eprintfn "warning: could not read %s: %s" failure.Path failure.Reason

        if List.contains "--json" args then
            if List.contains "--brief" args then
                printfn "%s" (Retrieval.toJsonBrief answer)
            else
                printfn "%s" (Retrieval.toJson answer)
        else
            printfn "%s" (Retrieval.toText answer)

        0
    with ex ->
        // A host failure is reported as a failure, never as an empty result.
        eprintfn "error: %s" ex.Message
        1
