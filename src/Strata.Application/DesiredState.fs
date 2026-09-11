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
          Declarations: (QualifiedName * string) list }

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
                        | Unmodelled _
                        | DeclarationFailed _ -> None)

                for declaration in declarations do
                    match declaration with
                    | Declared object' -> objects.Add object'
                    | DeclaredIndex (table, index) -> indexes.Add(table, index)
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

        // Indexes are declared in their own files but live on a table, so they
        // are attached once every file has been read.
        //
        // An index naming a table the project does not declare is a FAILURE,
        // not something to drop quietly: the project is asserting an index on
        // something it does not own, and acting on half of that is worse than
        // acting on none of it.
        let declaredIndexes = List.ofSeq indexes

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

                    if List.isEmpty attached then o else TableObject { t with Indexes = attached }
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

        let allFailures = loadFailures @ duplicates @ orphanIndexes

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
                      "view_definitions", NotRequested
                      "routines", NotRequested ] }
          Failures = allFailures
          Declarations = List.ofSeq declarations' }

    /// Schemas the project actually declared objects in.
    ///
    /// Distinct from the manifest's `managedSchemas`: the manifest says what
    /// MAY be changed, this says what was DECLARED. A schema listed as managed
    /// with no declared objects is the dangerous case — desired state would
    /// appear to say "this schema should be empty" — so a caller needs both to
    /// tell an empty declaration from an absent one.
    let schemasDeclaredIn (loaded: Loaded) = declaredSchemas loaded.Snapshot.Objects
