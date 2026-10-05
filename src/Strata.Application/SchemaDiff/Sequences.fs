namespace Strata.Application.SchemaDiff

open System
open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Semantic.AnalysisScope
open Strata.Analysis.DialectPort
open Strata.Analysis.ProposedChange

module Sequences =

    /// Sequences, compared on every property the model carries.
    ///
    /// Not by presence alone, as views and routines are: a sequence's increment
    /// and bounds are structure, they are declared in the file, and the catalog
    /// reports them exactly. There is nothing here that cannot be compared —
    /// except the CURRENT VALUE, which is not in the model at all because it is
    /// data, changes on every `nextval`, and "correcting" it would hand out a
    /// number twice.
    let compare (policy: RemovalPolicy) (inputs: Inputs) : FamilyDiff =
        let declared = SnapshotObjects.sequences inputs.Desired
        let deployed = SnapshotObjects.sequences inputs.Actual

        let shape (sq: Sequence) =
            sprintf
                "%s|%d|%d|%d|%d|%b"
                sq.DataType
                sq.Increment
                sq.MinValue
                sq.MaxValue
                sq.Cache
                sq.Cycle

        let created =
            declared
            |> List.filter (fun d -> not (deployed |> List.exists (fun a -> Names.same a.Name d.Name)))
            |> List.map (fun d -> Ok(CreateSequence d.Name))

        // START is excluded from `shape` deliberately. It only takes effect
        // when the sequence is created or explicitly restarted, so a deployed
        // sequence that has moved past its start is not a difference — and
        // proposing one would mean resetting a live counter.
        let altered =
            declared
            |> List.choose (fun d ->
                deployed
                |> List.tryFind (fun a -> Names.same a.Name d.Name)
                |> Option.bind (fun a -> if shape a <> shape d then Some(Ok(AlterSequence d.Name)) else None))

        let dropped =
            deployed
            |> List.filter (fun a -> not (declared |> List.exists (fun d -> Names.same d.Name a.Name)))
            |> List.map (fun a ->
                DropSafety.objectRemoval
                    policy
                    a.Name
                    a.Scope
                    { OutsideManaged = "a sequence outside the project's managed schemas"
                      ExtensionOwned = "an extension owns this sequence"
                      Incomplete = "absent from desired state, but desired state did not load completely"
                      DropsNotEnabled = "this sequence would be dropped; pass --allow-drops" }
                    (DropSequence a.Name))

        created @ altered @ dropped |> FamilyDiff.ofDifferences
