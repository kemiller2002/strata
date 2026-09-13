/// Diffing desired state against a target, and executing the result.
///
/// Authority for: everything `plan`, `apply` and `deploy` do once desired state
/// exists.
///
/// ## Why one module for three commands
///
/// The three differ in exactly one thing: where the `ResolvedDesiredState` came
/// from. `plan` and `apply` read a project directory and resolve it now;
/// `deploy` reads an artifact something else resolved earlier
/// (`DF-STRATA-2026-2F6B`). After that point they must behave identically, and
/// the only way to be sure of that is for there to be one implementation rather
/// than two that agree today.
///
/// That is not a style preference. The awkward-forms corpus spent its whole
/// life testing a pipeline that did not ship, because a second copy of part of
/// the pipeline had been written alongside the first and drifted (`WI-0093`).
/// A second copy of the DEPLOY path would drift the same way, and the symptom
/// would be that what you tested is not what you shipped — which is the exact
/// guarantee `deploy` exists to make.
module Strata.Cli.Deployment

open System
open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Semantic.AnalysisScope
open Strata.Analysis
open Strata.Application
open Strata.Host.Files
open Strata.Host.Postgres
open Strata.Host.PgParser

/// What the caller asked for, separated from where desired state came from.
type Options =
    { /// Propose removals. Absence is never authority to delete (NG-006), so
      /// this is opt-in and stays so.
      AllowDrops: bool
      /// Execute, rather than stopping after the plan.
      Apply: bool
      /// The second half of executing. Without it `apply` dry-runs and stops.
      Confirm: bool
      /// Accept the gate's requires-approval findings. Never answers a BLOCK.
      Approve: bool
      Json: bool
      Brief: bool
      /// Where the application SQL lives — the one thing neither a directory
      /// tree nor an artifact can say.
      CorpusRoots: string list

      /// Report whether the target matches, and answer with that alone.
      ///
      /// `drift` asks a different question from `plan`, and the difference is
      /// the exit code. `plan` answers "may this be applied?", which is the
      /// gate's verdict; `drift` answers "does this server still match?", which
      /// the gate has no opinion about. A monitoring host wants the second: a
      /// requires-approval verdict on a server that MATCHES is not an incident,
      /// and a clean-gated difference on one that does not is.
      DriftOnly: bool }

/// Run the whole deployment path against `connectionString`.
///
/// `desired` is passed separately from `resolved.Declared.Snapshot` because the
/// two differ for `plan`: a project with an unreadable file yields an
/// INCOMPLETE snapshot, which suppresses every drop. `compile` refuses such a
/// project outright, so for `deploy` they are the same value.
///
/// `managedSchemas` likewise. For a project it is the directory names; for an
/// artifact it is derived from the objects, which compile guarantees is the
/// same set because an empty schema directory does not compile.
let run
    (parser: DialectPort.IDialectParser)
    (connectionString: string)
    (options: Options)
    (managedSchemas: string list)
    (desired: SchemaSnapshot)
    (resolved: ResolvedDesiredState)
    : int =

    let declared = resolved.Declared
    let allowDrops = options.AllowDrops
    let wantsApply = options.Apply

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

    // Where the application SQL lives is the one thing neither a directory tree
    // nor an artifact can say, so the caller supplies it. Without it the gate
    // can still run, but it can only ever answer requires-approval on a
    // removal: "no dependency found" is not "no dependency exists" when nothing
    // was searched.
    let corpusRoots = options.CorpusRoots

    if List.isEmpty corpusRoots then
        eprintfn "warning: no SQL corpus given (--corpus, or corpusRoots in strata.json for plan)."
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
                ManagedSchemas = managedSchemas
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
                NormalisedDomains = resolved.NormalisedDomains
                Renames = renames }
    let gate = DeploymentGate.run graph scope diff.Changes

    if options.Json then
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

    // `drift` says this better in its own words, and says it about the right
    // thing: "nothing to apply" is an answer to a question drift did not ask.
    if converged && not options.DriftOnly then
        printfn ""
        printfn "Database already matches desired state; nothing to apply."

    if options.DriftOnly then
        printfn ""

        if converged then
            printfn "NO DRIFT: the target still matches the artifact."
            0
        else
            printfn
                "DRIFT: the target differs from the artifact in %d way(s), listed above."
                (List.length diff.Changes)

            printfn "       Nothing was changed. `strata deploy` is what changes a database."
            1

    elif not wantsApply then
        if converged then 0 else DeploymentGate.Verdict.exitCode gate.Verdict
    elif converged then
        0
    else

    // Everything below is the only irreversible thing Strata does,
    // so each refusal is separate and each says which one fired.
    let unwritable =
        diff.Statements |> List.filter (fun s -> s.Sql.IsNone)

    let approved = options.Approve

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

    elif not options.Confirm then
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
