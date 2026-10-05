namespace Strata.Application.SchemaDiff

open System
open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Semantic.AnalysisScope
open Strata.Analysis.DialectPort
open Strata.Analysis.ProposedChange

module Enums =

    /// Enumerated types, compared on their VALUES and their ORDER.
    ///
    /// ## Three outcomes, and only one of them is a change
    ///
    /// PostgreSQL lets you ADD a label, at a position. It does not let you
    /// remove one — `ALTER TYPE ... DROP VALUE` is a syntax error, not a
    /// privilege problem — and it does not let you reorder them. Measured
    /// against a live server, not taken from the documentation.
    ///
    /// So a declared type that has gained labels converges, and one that has
    /// LOST a label or moved one cannot, at all, without recreating the type and
    /// everything declared with it. Proposing a change Strata cannot write would
    /// produce a plan that fails halfway; saying nothing would report a project
    /// as deployed when it is not. Both are disclosures instead, naming what
    /// would have to happen.
    ///
    /// ## Prefix, not subset
    ///
    /// The declared values must START with the deployed ones, in order. That is
    /// stricter than "contains every deployed label" and it is the correct test:
    /// the only edit PostgreSQL permits is inserting new labels, so any deployed
    /// label whose relative order has changed is a reorder, which it cannot do.
    let compare (policy: RemovalPolicy) (inputs: Inputs) : FamilyDiff =
        let declared = SnapshotObjects.enums inputs.Desired
        let deployed = SnapshotObjects.enums inputs.Actual

        let created =
            declared
            |> List.filter (fun d -> not (deployed |> List.exists (fun a -> Names.same a.Name d.Name)))
            |> List.map (fun d -> Ok(CreateEnumType d.Name))

        /// The labels a deployed type is missing, each with the label it must
        /// follow in the DECLARED order — which is what `ALTER TYPE ... ADD
        /// VALUE AFTER` needs.
        let additions (declaredValues: string list) (deployedValues: string list) =
            declaredValues
            |> List.mapi (fun i v -> i, v)
            |> List.filter (fun (_, v) -> not (List.contains v deployedValues))
            |> List.map (fun (i, v) -> v, (if i = 0 then None else List.tryItem (i - 1) declaredValues))

        let altered =
            declared
            |> List.collect (fun d ->
                match deployed |> List.tryFind (fun a -> Names.same a.Name d.Name) with
                | None -> []
                | Some a ->
                    let kept = d.Values |> List.filter (fun v -> List.contains v a.Values)

                    if kept <> a.Values then
                        // Either a deployed label is gone from the project, or
                        // the shared labels are in a different order. PostgreSQL
                        // can do neither.
                        let lost = a.Values |> List.filter (fun v -> not (List.contains v d.Values))

                        [ Microsoft.FSharp.Core.Error
                              { Object = d.Name
                                Reason = NotCompared
                                Detail =
                                  if List.isEmpty lost then
                                      sprintf
                                          "the project orders this type's values differently from the database (project: %s; database: %s). PostgreSQL cannot reorder an enum's values, so nothing here can converge without recreating the type and everything declared with it."
                                          (String.concat ", " d.Values)
                                          (String.concat ", " a.Values)
                                  else
                                      sprintf
                                          "the database has %d value(s) this project does not declare: %s. PostgreSQL has no ALTER TYPE ... DROP VALUE, so they cannot be removed — recreate the type, or declare them."
                                          (List.length lost)
                                          (String.concat ", " lost) } ]
                    else
                        additions d.Values a.Values
                        |> List.map (fun (value, after) -> Ok(AddEnumValue(d.Name, value, after))))

        let dropped =
            deployed
            |> List.filter (fun a -> not (declared |> List.exists (fun d -> Names.same d.Name a.Name)))
            |> List.map (fun a ->
                DropSafety.objectRemoval
                    policy
                    a.Name
                    a.Scope
                    { OutsideManaged = "a type outside the project's managed schemas"
                      ExtensionOwned = "an extension owns this type"
                      Incomplete = "absent from desired state, but desired state did not load completely"
                      DropsNotEnabled = "this type would be dropped; pass --allow-drops" }
                    (DropEnumType a.Name))

        created @ altered @ dropped |> FamilyDiff.ofDifferences
