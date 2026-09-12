module Strata.Cli.Program

open System
open System.IO
open Strata.Semantic.Identity
open Strata.Semantic.Schema
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
                                   1 block, 2 requires approval — and 0 when
                                   the database ALREADY MATCHES, whatever the
                                   verdict, because an empty plan from a diff
                                   is convergence rather than a problem.
  compile [--project <dir>]        Read the project and ask a server what the
          --out <file>             DECLARED side means — view text, defaults,
                                   check and policy expressions, reference rows
                                   through the real column types — and write it
                                   all to an artifact. Never looks at a target's
                                   schema, so the connection may be any
                                   PostgreSQL of the right version. REFUSES on
                                   any file it could not read or parse: an
                                   artifact is a claim that the project was read
                                   whole, and a deployment has no source tree to
                                   check that claim against. Exit 0 compiled,
                                   1 the project is wrong, 2 unreadable.
  apply [--project <dir>]          Execute the plan. Requires --confirm, and
                                   refuses unless the gate allows every change
                                   or --approve accepts its requires-approval
                                   findings. A block is never overridden.
  validate <file.sql>              Check every relation and column in a SQL file
                                   against the live schema. Exit 0 valid,
                                   1 provably wrong — a reference that does not
                                   exist, or SQL that does not parse —
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
  --approve          Accept the gate's requires-approval findings. This is the
                     human decision the gate is asking for, so it is recorded
                     in the output: each finding is restated as approved before
                     anything runs. It NEVER overrides a block — a block is
                     known breakage, not a judgement call — and it does not
                     enable removals, which need --allow-drops as well.
  --out <file>       Where `compile` writes the artifact
  --allow-drops      Propose removals. Without it, objects present in the
                     database and absent from the project are REPORTED but
                     never proposed for dropping.
  --json             Machine-readable output (default is human-readable)
  --brief            With --json, replace the scope block with a digest. Fetch
                     the full scope once via `strata scope`. Caveats that change
                     how a result must be read are ALWAYS kept inline.

PROJECT LAYOUT
  <project>/schema/<schema>/<kind>/<name>.sql, one object per file. `kind` is
  the directory name and is yours to choose; tables/, views/, routines/,
  indexes/, triggers/, sequences/, policies/, extensions/ and grants/ are the
  conventional ones.

  strata.json is REQUIRED and lists every object file explicitly:

      {
        "include": [
          "schema/app/tables/customer.sql",
          "schema/app/grants/customer.sql"
        ]
      }

  Strata owns the directory it is given. A .sql file under the schema root that
  `include` does not list FAILS the load, and so does a listed path that is not
  there. Both are errors, not warnings — a build that ignores warnings is what a
  warning is, and both have a cheap fix: list the file, or move it out. There is
  no exclude list; moving a file out of the directory is how you exclude it.

  No globs. "schema/**/*.sql" is a directory walk wearing a manifest's clothes:
  drop a file in and the build takes it.

  The managed schemas are the directory names under the schema root — there is
  no managedSchemas setting. A directory name must equal the schema name exactly
  and match [a-z_][a-z0-9_]*, so that one name works on Linux, macOS and Windows
  alike. A schema in the database with no directory is left alone and reported.
  An EMPTY schema directory is an error: a directory declaring nothing cannot be
  told from one saying its schema should be empty, and the second is authority to
  drop everything in it.

  grants/<object>.sql holds GRANT statements, on a table, view or sequence, on
  a SCHEMA, or on a function or procedure:

      GRANT USAGE ON SCHEMA ref TO app_user;
      GRANT SELECT, INSERT ON ref.account TO app_user;
      GRANT EXECUTE ON FUNCTION ref.balance(bigint) TO app_user;
      GRANT SELECT (id, email) ON ref.customer TO app_user;

  USAGE on the schema is what makes the names inside it reachable, so a project
  that grants on a table and nothing on its schema has granted nothing usable.
  A routine grant must name its argument types: without them PostgreSQL picks
  whichever overload is deployed, which is not something a file can declare.

  Declaring a grant takes ownership of THAT GRANTEE's privileges on THAT
  object — a grantee the project never names keeps what it has and is reported,
  so managing app_user cannot silently revoke a replication or monitoring role.

  Column privileges are a separate store from the table's, not a narrower view
  of it: a role with table-wide SELECT reads every column. So declaring a
  column grant claims that grantee's TABLE-wide privileges too, and a standing
  table-wide SELECT is proposed for revoking — otherwise "only these columns"
  would add access and narrow none. Another grantee is still untouched.

  extensions/<name>.sql holds a CREATE EXTENSION:

      CREATE EXTENSION pgcrypto;
      CREATE EXTENSION citext VERSION '1.6';

  A version the file does not pin is not compared: asking for the extension is
  not asking for whichever version happens to be installed. An extension is
  installed and version-updated, and NEVER dropped — citext alone owns 88
  catalog objects and DROP EXTENSION takes every one, which is more than
  anything else here removes from a single statement. An extension the project
  does not declare is reported and left alone, and so are the objects any
  extension owns.

  policies/<name>.sql holds a CREATE POLICY, and the ALTER TABLE that switches
  row-level security on:

      CREATE POLICY doc_tenant ON app.doc FOR ALL TO app_user
          USING (tenant = current_setting('app.tenant'));

      ALTER TABLE app.doc ENABLE ROW LEVEL SECURITY;

  A policy Strata declares, it creates and replaces. A policy it does NOT
  declare is reported and never dropped, --allow-drops included: dropping one
  breaks no query and loses no row, it makes rows that were hidden visible to
  whoever can read the table, and nothing records that they used to be hidden.
  That is the least recoverable mistake available, so it is the one thing drops
  do not reach — the same rule reference rows get.

  Two states are worth knowing because both look like an ordinary table, and a
  plan says which holds for every managed table: row-level security enabled
  with no policies hides every row from every role except the owner, and
  policies on a table where it is disabled restrict nothing at all.

  Switching row-level security on runs LAST in a plan, after any reference rows.
  FORCE makes policies apply to the table's own owner, so enabling it first has
  the server reject the plan's own inserts and roll the whole thing back.

  Two things about privileges are reported and never changed. Every function
  starts with EXECUTE granted to PUBLIC, so a project that does not declare
  PUBLIC is told that PUBLIC can still call its functions. And WITH GRANT
  OPTION is not modelled: a file cannot ask for it, and a grantee that already
  has it is disclosed rather than left to look like an ordinary grant.

  The <schema> directory names the schema, and Strata CREATES it if the
  database does not have it — so a project applies against an empty database.
  It never drops one: a schema holds objects, including any the project never
  declared.

  data/<table>.sql holds a reference table's ROWS, as plain INSERT statements:

      INSERT INTO ref.account_type (id, code, label) VALUES
          (1, 'checking', 'Checking'),
          (2, 'savings',  'Savings');

  Those rows are diffed like any other declared state, so a dry run says which
  will be inserted and which updated. Every value must be a literal; a value
  the file cannot fix belongs in the column's DEFAULT. Rows are matched on the
  table's primary key, and a column the file does not name is a column it says
  nothing about.

  Declaring rows does NOT claim the table's other rows. A row the project does
  not declare is reported and never deleted, `--allow-drops` included: user
  data may reference it.

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

    /// `compile` reads the project and asks a server what the DECLARED side
    /// means. It never looks at the target's schema — that is the half of a
    /// deployment that cannot be compiled (`DF-STRATA-2026-2F6B`) — so the
    /// connection it takes may be any PostgreSQL of the right version.
    let wantsCompile =
        match positional with
        | "compile" :: _ -> true
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
        | "compile" :: _ -> Error "compile takes no positional arguments; use --project and --out"
        | command :: _ -> Error(sprintf "unknown or incomplete command: %s" command)
        | [] -> Error "no command given"

    if wantsCompile then
        match valueOf "--out" args with
        | None ->
            eprintfn "error: compile needs --out <file>, the artifact to write."
            2
        | Some outputPath ->
            let parser = PgParserAdapter.PostgresParser() :> Strata.Analysis.DialectPort.IDialectParser
            Compile.run parser connectionString projectRoot outputPath

    elif wantsPlan then
        try
            let parser = PgParserAdapter.PostgresParser() :> Strata.Analysis.DialectPort.IDialectParser

            match Compile.load parser projectRoot with
            | Error message ->
                eprintfn "error: %s" message
                2
            | Ok loaded ->
                let project = loaded.Project
                let declared = loaded.Declared

                // A file that could not even be located or read never reached
                // the loader, so its failure has to be folded in here or the
                // desired state would call itself complete while missing it.
                // `compile` refuses instead; the two share the reading so they
                // cannot disagree about what the project SAYS.
                let desired = Compile.snapshot loaded

                for path, reason in loaded.Problems do
                    eprintfn "warning: %s: %s" path reason

                // Taken BEFORE introspection so it covers the whole window in
                // which the plan is computed, not just the tail of it.
                let fingerprintBeforePlanning =
                    match Execution.fingerprint connectionString with
                    | Ok value -> value
                    | Microsoft.FSharp.Core.Error _ -> ""

                let actual = CatalogIntrospection.introspect connectionString

                // Read separately from the snapshot because an EMPTY schema has
                // no objects to appear in it. Without this, "the project
                // declares objects in ref" and "ref exists" could not be told
                // apart, and a first apply against a fresh database failed on
                // the first CREATE TABLE.
                let existingSchemas =
                    match CatalogIntrospection.readSchemas connectionString with
                    | Ok names -> Some names
                    | Microsoft.FSharp.Core.Error message ->
                        eprintfn "warning: could not read the database's schema list (%s)." message
                        eprintfn "         Declared schemas will be reported as not-checked."
                        None

                // Read for the same reason and with the same care: an ACL that
                // could not be read is not an absent privilege.
                let actualGrants =
                    match CatalogIntrospection.readGrants connectionString with
                    | Ok grants -> Some grants
                    | Microsoft.FSharp.Core.Error message ->
                        eprintfn "warning: could not read the database's privileges (%s)." message
                        eprintfn "         Declared grants will be reported as not-compared."
                        None

                // Reported, not compared — but READ, which is the point. Nothing
                // looked at row-level security before, so a table with it
                // enabled and no policies, hiding every row from every role,
                // rendered exactly like a table with no row-level security at
                // all: a clean plan and not a word about either.
                //
                // The snapshot's own `rls_policies` category stays
                // `NotRequested`, the same as `grants`: the snapshot genuinely
                // does not carry them, and this is read alongside it.
                let actualRowLevelSecurity =
                    match CatalogIntrospection.readRowLevelSecurity connectionString with
                    | Ok state -> Some(Ok state)
                    | Microsoft.FSharp.Core.Error message ->
                        eprintfn "warning: could not read the database's row-level security (%s)." message
                        eprintfn "         Which rows any role can see is unknown, and will be reported as such."
                        // `Some (Error _)`, never `None`: the CLI DID ask, and
                        // that failure is reported. `None` is reserved for a
                        // caller that never asked.
                        Some(Microsoft.FSharp.Core.Error message)

                let actualExtensions =
                    match CatalogIntrospection.readExtensions connectionString with
                    | Ok installed -> Some(Ok installed)
                    | Microsoft.FSharp.Core.Error message ->
                        eprintfn "warning: could not read the database's extensions (%s)." message
                        eprintfn "         Declared extensions will be reported as not-compared."
                        Some(Microsoft.FSharp.Core.Error message)

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

                // Everything about the declared side that only a server can
                // settle: view text, defaults, check and policy expressions,
                // and reference rows through the real column types. One named
                // boundary, because it is exactly what `strata compile` will
                // produce and hand to `strata deploy`
                // (`DF-STRATA-2026-2F6B`).
                let resolved = Resolution.resolve connectionString declared

                for warning in resolved.Warnings do
                    eprintfn "warning: %s" warning

                for failure in resolved.DataFailures do
                    eprintfn "warning: %s: %s" failure.Table failure.Reason

                // Rename intent, read from the raw file text: libpg_query
                // discards comments, so there is nothing to read in the tree.
                let renames =
                    declared.Declarations
                    |> List.map (fun (name, text) ->
                        let annotations = RenameAnnotations.read text

                        let qualify (raw: string) =
                            match raw.Split('.') with
                            | [| schema; object' |] ->
                                QualifiedName.qualified
                                    (Identifier.unquoted (schema.Trim '"'))
                                    (Identifier.unquoted (object'.Trim '"'))
                            | _ ->
                                // An unqualified old name means the same schema
                                // the object is declared in. Reaching across
                                // schemas has to be spelled out.
                                match name.Schema with
                                | Some schema ->
                                    QualifiedName.qualified schema (Identifier.unquoted (raw.Trim '"'))
                                | None -> QualifiedName.unqualified (Identifier.unquoted (raw.Trim '"'))

                        ({ Object = name
                           RenamedFrom = annotations.Object |> Option.map qualify
                           Columns = annotations.Columns }: SchemaDiff.DeclaredRename))
                    |> List.filter (fun r -> r.RenamedFrom.IsSome || not (List.isEmpty r.Columns))

                for r in renames do
                    match r.RenamedFrom with
                    | Some from ->
                        eprintfn
                            "note: %s declares a rename from %s"
                            (QualifiedName.display r.Object)
                            (QualifiedName.display from)
                    | None -> ()

                let diff =
                    SchemaDiff.run
                        { SchemaDiff.Inputs.between desired actual with
                            AllowDrops = allowDrops
                            ManagedSchemas = project.Schemas
                            Declarations = declared.Declarations
                            TriggerDeclarations = declared.TriggerDeclarations
                            ExistingSchemas = existingSchemas
                            DeclaredGrants = declared.Grants
                            ActualGrants = actualGrants
                            ActualRowLevelSecurity = actualRowLevelSecurity
                            PolicyDeclarations = declared.PolicyDeclarations
                            DeclaredPolicies = resolved.Policies
                            DeclaredRowSecurity = declared.RowSecurity
                            DeclaredExtensions = declared.Extensions
                            ActualExtensions = actualExtensions
                            Data = resolved.Data
                            DataFailures = resolved.DataFailures
                            NormalisedViews = resolved.NormalisedViews
                            NormalisedTables = resolved.NormalisedTables
                            Renames = renames }
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

                let approved = List.contains "--approve" args

                // `--approve` answers requires-approval, which is precisely the
                // question the gate asks a human. It never answers a BLOCK:
                // that verdict means Strata found the breakage, not that it
                // could not tell, and a flag that overrode both would make the
                // three verdicts two.
                let gateSatisfied =
                    match gate.Verdict with
                    | DeploymentGate.Allow -> true
                    | DeploymentGate.RequiresApproval -> approved
                    | DeploymentGate.Block -> false

                if not gateSatisfied then
                    eprintfn ""
                    eprintfn "REFUSED: the gate did not allow this plan (%s)." (DeploymentGate.Verdict.tag gate.Verdict)

                    match gate.Verdict with
                    | DeploymentGate.RequiresApproval ->
                        eprintfn "         Nothing was executed. Address the findings above, or pass --approve"
                        eprintfn "         to record that a human accepted them."
                    | DeploymentGate.Block ->
                        // Saying this explicitly matters: someone who just
                        // learned about --approve will reach for it here, and
                        // the answer is that it does not apply.
                        eprintfn "         Nothing was executed. A block is known breakage, not a judgement"
                        eprintfn "         call, and --approve does not override it. Fix what the findings name."
                    | DeploymentGate.Allow -> ()

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
                    // An approval nobody can see afterwards is not a decision
                    // anyone can review. Each finding the human accepted is
                    // restated here, in the run's own output, before it runs.
                    if approved && gate.Verdict = DeploymentGate.RequiresApproval then
                        printfn ""
                        printfn "APPROVED by --approve:"

                        for f in gate.Findings do
                            if f.Verdict = DeploymentGate.RequiresApproval then
                                printfn "  %s" f.Detected

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
