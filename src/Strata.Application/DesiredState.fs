namespace Strata.Application

open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Semantic.AnalysisScope
open Strata.Analysis.DialectPort

/// Assembling desired state from declared object files (PR-025).
///
/// Authority for: turning a project's object files into a `SchemaSnapshot`.
///
/// The output is the SAME type catalog introspection produces, which is the
/// point: a diff compares two `SchemaSnapshot`s and neither side is privileged.
/// `DF-STRATA-2026-B1E7`.
///
/// ## Completeness means something different on this side
///
/// An introspected snapshot is incomplete when the server would not tell
/// Strata something. A DECLARED snapshot is incomplete when a file did not
/// parse, sat outside the layout, or declared an object type Strata does not
/// model yet. Both produce the same consequence — absence cannot be trusted —
/// which is exactly why the same `Completeness` carries it. A diff that read a
/// half-loaded desired state and proposed dropping the rest is the failure
/// `NG-006` and §1437 exist to prevent, and it is prevented here by refusing
/// to call the snapshot complete rather than by a check further downstream.
module DesiredState =

    /// A file that did not become a declared object.
    type LoadFailure =
        { Path: string
          Reason: string }

    type Loaded =
        { Snapshot: SchemaSnapshot
          Failures: LoadFailure list
          /// The verbatim text that declared each object, when the file
          /// declared exactly one.
          ///
          /// Carried because CREATING an object from a reconstructed parse
          /// tree loses whatever the model does not carry — a column default,
          /// a check expression — and 96% of real tables have at least one
          /// (`EV-STRATA-2026-D3A8`). The file already holds exactly the DDL
          /// the author wrote, so executing it is both simpler and strictly
          /// more faithful than deparsing protobuf back into SQL.
          ///
          /// Only single-declaration files are recorded. Executing a file that
          /// declares two tables to create ONE of them would create the other
          /// as a side effect, which is a change nobody planned.
          Declarations: (QualifiedName * string) list

          /// The verbatim text that declared each trigger, keyed by the table
          /// it fires on and its own name.
          ///
          /// Kept apart from `Declarations` because a trigger name is not a
          /// `QualifiedName`: trigger names are scoped to a TABLE, not to a
          /// schema, so two tables may each carry a trigger called
          /// `set_updated_at` and neither is the other.
          ///
          /// Verbatim for a stronger reason than tables have. A trigger's
          /// `WHEN` clause and its `UPDATE OF` column list are not recoverable
          /// from the model, so a reconstruction would silently create a
          /// trigger that fires more often than the file asked for.
          TriggerDeclarations: ((QualifiedName * Identifier) * string) list }

    /// Declared schema names, from the objects actually loaded.
    let private declaredSchemas (objects: SchemaObject list) =
        objects
        |> List.choose (fun o -> (SchemaObject.name o).Schema)
        |> List.map Identifier.folded
        |> List.distinct

    /// Assemble a snapshot from already-read files.
    ///
    /// Takes `(path, contents)` rather than reading disk, so the whole
    /// assembly is testable without a filesystem — the same reason
    /// `CorpusPipeline.analyse` takes sources rather than a directory.
    let load (parser: IDialectParser) (files: (string * string) list) : Loaded =
        let objects = ResizeArray<SchemaObject>()
        let failures = ResizeArray<LoadFailure>()
        let declarations' = ResizeArray<QualifiedName * string>()
        let indexes = ResizeArray<QualifiedName * Index>()
        let triggers = ResizeArray<QualifiedName * Trigger>()
        let triggerDeclarations = ResizeArray<(QualifiedName * Identifier) * string>()

        for path, contents in files do
            let declarations = parser.ParseObjectDefinitions contents

            match declarations with
            | [] ->
                failures.Add
                    { Path = path
                      Reason = "file declares no statements" }
            | _ ->
                let declaredHere =
                    declarations
                    |> List.choose (function
                        | Declared o -> Some o
                        | DeclaredIndex _
                        | DeclaredTrigger _
                        | Unmodelled _
                        | DeclarationFailed _ -> None)

                for declaration in declarations do
                    match declaration with
                    | Declared object' -> objects.Add object'
                    | DeclaredIndex (table, index) -> indexes.Add(table, index)
                    | DeclaredTrigger (table, trigger) -> triggers.Add(table, trigger)
                    | Unmodelled detail -> failures.Add { Path = path; Reason = detail }
                    | DeclarationFailed error ->
                        failures.Add
                            { Path = path
                              Reason = sprintf "does not parse: %s" error.Message }

                match declaredHere with
                | [ single ] -> declarations'.Add(SchemaObject.name single, contents)
                // Two or more objects in one file: the text cannot be attributed
                // to either, so neither gets it and both fall back to
                // reconstruction.
                | _ -> ()

                // Same rule for a trigger file, and the same reason: a file
                // holding two CREATE TRIGGERs cannot have its text attributed
                // to either, so executing it to create one would create both.
                match
                    declarations
                    |> List.choose (function
                        | DeclaredTrigger (table, trigger) -> Some(table, trigger)
                        | _ -> None)
                with
                | [ (table, trigger) ] when List.isEmpty declaredHere ->
                    triggerDeclarations.Add((table, trigger.Name), contents)
                | _ -> ()

        // Indexes are declared in their own files but live on a table, so they
        // are attached once every file has been read.
        //
        // An index naming a table the project does not declare is a FAILURE,
        // not something to drop quietly: the project is asserting an index on
        // something it does not own, and acting on half of that is worse than
        // acting on none of it.
        let declaredIndexes = List.ofSeq indexes
        let declaredTriggers = List.ofSeq triggers

        let declaresTable (name: QualifiedName) =
            objects
            |> Seq.exists (fun o ->
                match o with
                | TableObject t -> QualifiedName.display t.Name = QualifiedName.display name
                | ViewObject _
                | RoutineObject _ -> false)

        let orphanIndexes =
            declaredIndexes
            |> List.filter (fun (table, _) ->
                not (
                    objects
                    |> Seq.exists (fun o ->
                        QualifiedName.display (SchemaObject.name o) = QualifiedName.display table)))
            |> List.map (fun (table, index) ->
                { Path = QualifiedName.display table
                  Reason =
                    sprintf
                        "index '%s' is declared on a table this project does not declare"
                        index.Name.Text })

        // A trigger on an object the project does not declare AS A TABLE.
        // Triggers on views are legal PostgreSQL — `INSTEAD OF` triggers only
        // exist on views — and Strata models triggers on tables alone, so a
        // trigger on a declared view lands here too. That is the right place
        // for it: a failure the project can see, rather than a declaration
        // that quietly disappears.
        let orphanTriggers =
            declaredTriggers
            |> List.filter (fun (table, _) -> not (declaresTable table))
            |> List.map (fun (table, trigger) ->
                { Path = QualifiedName.display table
                  Reason =
                    sprintf
                        "trigger '%s' is declared on '%s', which this project does not declare as a table"
                        trigger.Name.Text
                        (QualifiedName.display table) })

        let withIndexes =
            objects
            |> Seq.map (fun o ->
                match o with
                | TableObject t ->
                    let attached =
                        declaredIndexes
                        |> List.filter (fun (table, _) ->
                            QualifiedName.display table = QualifiedName.display t.Name)
                        |> List.map snd

                    let attachedTriggers =
                        declaredTriggers
                        |> List.filter (fun (table, _) ->
                            QualifiedName.display table = QualifiedName.display t.Name)
                        |> List.map snd

                    if List.isEmpty attached && List.isEmpty attachedTriggers then
                        o
                    else
                        TableObject
                            { t with
                                Indexes = (if List.isEmpty attached then t.Indexes else attached)
                                Triggers =
                                    (if List.isEmpty attachedTriggers then t.Triggers else attachedTriggers) }
                | ViewObject _
                | RoutineObject _ -> o)
            |> List.ofSeq

        let loaded = withIndexes
        let loadFailures = List.ofSeq failures

        // One object per file is the layout contract. More than one is not an
        // error, but it does mean "which file owns this object" has two
        // answers, so it is reported.
        let duplicates =
            loaded
            |> List.countBy (fun o -> QualifiedName.display (SchemaObject.name o))
            |> List.filter (fun (_, count) -> count > 1)
            |> List.map (fun (name, count) ->
                { Path = name
                  Reason = sprintf "declared %d times across the project" count })

        let allFailures = loadFailures @ duplicates @ orphanIndexes @ orphanTriggers

        let state reason =
            if List.isEmpty allFailures then Complete else Partial reason

        { Snapshot =
            { Objects = loaded
              // A declared snapshot has no server, and saying `None` is the
              // honest answer. A diff needs it to compare dialect majors and
              // must get the version from the LIVE side.
              ServerVersion = None
              Completeness =
                Completeness.ofList
                    [ "relations",
                      state (sprintf "%d file(s) did not become a declared object" (List.length allFailures))
                      "columns",
                      state "column declarations are only as complete as the files that parsed"
                      "constraints",
                      state "constraint declarations are only as complete as the files that parsed"
                      // A project that declares NO index file is not saying
                      // "this schema has no indexes" — it is saying nothing
                      // about indexes at all, and the difference decides
                      // whether every existing index is a drop candidate.
                      // Declaring one index is how a project takes ownership
                      // of them, exactly as declaring one table does.
                      "indexes",
                      (if List.isEmpty declaredIndexes then NotRequested
                       else state "index declarations are only as complete as the files that parsed")
                      // Identical rule, identical reason. A project with no
                      // trigger file says nothing about triggers; declaring
                      // one takes ownership of all of them on the tables it
                      // declares.
                      "triggers",
                      (if List.isEmpty declaredTriggers then NotRequested
                       else state "trigger declarations are only as complete as the files that parsed")
                      "view_definitions", NotRequested
                      "routines", NotRequested ] }
          Failures = allFailures
          Declarations = List.ofSeq declarations'
          TriggerDeclarations = List.ofSeq triggerDeclarations }

    /// Schemas the project actually declared objects in.
    ///
    /// Distinct from the manifest's `managedSchemas`: the manifest says what
    /// MAY be changed, this says what was DECLARED. A schema listed as managed
    /// with no declared objects is the dangerous case — desired state would
    /// appear to say "this schema should be empty" — so a caller needs both to
    /// tell an empty declaration from an absent one.
    let schemasDeclaredIn (loaded: Loaded) = declaredSchemas loaded.Snapshot.Objects
