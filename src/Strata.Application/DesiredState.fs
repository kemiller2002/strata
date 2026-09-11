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
          Failures: LoadFailure list }

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

        for path, contents in files do
            let declarations = parser.ParseObjectDefinitions contents

            match declarations with
            | [] ->
                failures.Add
                    { Path = path
                      Reason = "file declares no statements" }
            | _ ->
                for declaration in declarations do
                    match declaration with
                    | Declared object' -> objects.Add object'
                    | Unmodelled detail -> failures.Add { Path = path; Reason = detail }
                    | DeclarationFailed error ->
                        failures.Add
                            { Path = path
                              Reason = sprintf "does not parse: %s" error.Message }

        let loaded = List.ofSeq objects
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

        let allFailures = loadFailures @ duplicates

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
                      // Indexes are separate statements in PostgreSQL and are
                      // not read yet, so the declared side cannot speak to
                      // them at all. Stated rather than omitted.
                      "indexes", NotRequested
                      "view_definitions", NotRequested
                      "routines", NotRequested ] }
          Failures = allFailures }

    /// Schemas the project actually declared objects in.
    ///
    /// Distinct from the manifest's `managedSchemas`: the manifest says what
    /// MAY be changed, this says what was DECLARED. A schema listed as managed
    /// with no declared objects is the dangerous case — desired state would
    /// appear to say "this schema should be empty" — so a caller needs both to
    /// tell an empty declaration from an absent one.
    let schemasDeclaredIn (loaded: Loaded) = declaredSchemas loaded.Snapshot.Objects
