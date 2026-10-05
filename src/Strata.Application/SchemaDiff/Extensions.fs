namespace Strata.Application.SchemaDiff

open System
open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Semantic.AnalysisScope
open Strata.Analysis.DialectPort
open Strata.Analysis.ProposedChange

module Extensions =

    /// Extensions a project declares, against what is installed.
    ///
    /// Created and version-updated. NEVER dropped: `citext` alone owns 88
    /// catalog objects and `DROP EXTENSION` takes every one, which is more than
    /// anything else in the vocabulary removes from a single statement. An
    /// extension the project does not declare is reported and left alone.
    ///
    /// A version the file did not pin is not compared. `CREATE EXTENSION
    /// pgcrypto` asks for the extension, not for the version that happens to be
    /// installed, so reading the absence as a demand would propose an update on
    /// every run — or worse, a downgrade the server refuses.
    let compare (inputs: Inputs) : FamilyDiff =
        let declared = inputs.DeclaredExtensions

        match inputs.ActualExtensions with
        | None -> FamilyDiff.ofDifferences []
        | Some (Microsoft.FSharp.Core.Error message) ->
            { Differences = []
              Disclosures =
                if List.isEmpty declared then
                    []
                else
                    [ { Object = QualifiedName.unqualified (Identifier.unquoted "(extensions)")
                        Reason = NotCompared
                        Detail =
                          sprintf
                              "the database's installed extensions could not be read (%s), so declared extensions were NOT compared"
                              message } ] }
        | Some (Ok installed) ->

        let changes =
            declared
            |> List.collect (fun d ->
                match installed |> List.tryFind (fun a -> Identifier.sameName a.Name d.Name) with
                | None -> [ Ok(CreateExtension d.Name) ]
                | Some a ->
                    [ match d.Version, a.Version with
                      | Some wanted, Some held when wanted <> held -> Ok(UpdateExtension(d.Name, wanted))
                      | _ -> ()

                      match d.Schema, a.Schema with
                      | Some wanted, Some held when not (Identifier.sameName wanted held) ->
                          if a.IsRelocatable then
                              Ok(SetExtensionSchema(d.Name, wanted))
                          else
                              // `plpgsql` is the common case and it is installed
                              // everywhere. Proposing a statement the server
                              // rejects would fail the whole plan.
                              Microsoft.FSharp.Core.Error
                                  { Object = QualifiedName.unqualified d.Name
                                    Reason = NotModelled
                                    Detail =
                                      sprintf
                                          "the project declares extension %s in schema %s and it is installed in %s, but the extension is not relocatable, so nothing can move it"
                                          d.Name.Text
                                          wanted.Text
                                          held.Text }
                      | _ -> () ])

        // Installed and undeclared. Reported, never dropped.
        let undeclared =
            installed
            |> List.filter (fun a -> not (declared |> List.exists (fun d -> Identifier.sameName d.Name a.Name)))
            |> List.map (fun a ->
                { Object = QualifiedName.unqualified a.Name
                  Reason = NotModelled
                  Detail =
                    sprintf
                        "extension %s is installed and the project does not declare it; an extension is never dropped because DROP EXTENSION takes every object it owns%s"
                        a.Name.Text
                        (match a.Version with
                         | Some v -> sprintf " (version %s)" v
                         | None -> "") })

        { Differences = changes; Disclosures = undeclared }
