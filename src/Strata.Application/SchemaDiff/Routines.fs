namespace Strata.Application.SchemaDiff

open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Analysis.ProposedChange

/// Functions and procedures beyond their presence: when one is redefined in
/// place, when it has to be dropped and created again, and when it may be
/// dropped at all (strata#28).
module Routines =

    /// A routine's identity: its name AND argument types. PostgreSQL allows
    /// overloads, so f(int) and f(text) are different objects and matching on
    /// name alone would read one as a redefinition of the other.
    let identity (r: Routine) =
        sprintf "routine:%s(%s)" (QualifiedName.display r.Name) (String.concat "," r.ArgumentTypes)

    /// Whether two routines of one identity hold different bodies.
    ///
    /// A routine body needs no shadow: PostgreSQL stores a classic
    /// `AS $$...$$` body verbatim in prosrc, so the declared text and the
    /// deployed text compare directly. A body the server holds only as a parse
    /// tree (SQL-standard BEGIN ATOMIC) or as a symbol name (C) is None on one
    /// side, so nothing is claimed and `Objects` discloses it instead.
    let bodiesDiffer (declared: Routine) (deployed: Routine) =
        match declared.Body, deployed.Body with
        | Some d, Some a -> d.Trim() <> a.Trim()
        | _ -> false

    let private all (snapshot: SchemaSnapshot) =
        snapshot.Objects |> List.choose (function RoutineObject r -> Some r | _ -> None)

    /// Every routine on both sides whose body differs, as (declared, deployed):
    /// the routines a plan redefines. The host asks the server about exactly
    /// these, before the plan, whether each can be replaced in place.
    let redefined (desired: SchemaSnapshot) (actual: SchemaSnapshot) =
        all desired
        |> List.choose (fun d ->
            all actual
            |> List.tryFind (fun a -> identity a = identity d && bodiesDiffer d a)
            |> Option.map (fun a -> d, a))

    /// A drop is refused while anything in the database depends on the
    /// routine — a view, a trigger, a default, another routine's body — and
    /// the refusal names them. A routine missing from what was read is
    /// refused too: unread is not "nothing depends on it". Strata does not cascade: what depends on the
    /// routine is the project's to change, not Strata's to take with it.
    let dependentsGuard (inputs: Inputs) (r: Routine) =
        let dependents =
            inputs.RoutineDependents
            |> Option.bind (List.tryPick (fun (key, ds) -> if key = identity r then Some ds else None))

        NoDependents(
            dependents,
            match dependents with
            | None -> sprintf "%s cannot be dropped: what depends on it could not be read" (identity r)
            | Some ds -> sprintf "%s cannot be dropped while these depend on it: %s" (identity r) (String.concat "; " ds)
        )

    /// A deployed routine that desired state does not declare.
    let removal (policy: RemovalPolicy) (inputs: Inputs) (scope: ManagementScope) (wording: ObjectRemovalWording) (r: Routine) =
        DropSafety.objectRemovalWith
            [ dependentsGuard inputs r ]
            policy
            r.Name
            scope
            wording
            (DropRoutine(r.Name, r.ArgumentTypes))

    /// A routine whose body changed.
    ///
    /// Replaced in place, unless the server refused `CREATE OR REPLACE` for it
    /// — a changed return type, parameter names or parameter defaults. Then it
    /// is dropped and created again from its file, and that drop is a removal,
    /// gated like every other: it needs `--allow-drops`, a desired state that
    /// loaded completely, and nothing in the database depending on it.
    let redefinition (policy: RemovalPolicy) (inputs: Inputs) (declared: Routine, deployed: Routine) =
        match inputs.RoutineRejections |> List.tryFind (fun (key, _) -> key = identity deployed) with
        | None -> [ Ok(ReplaceRoutine declared.Name) ]
        | Some (_, reason) ->
            let needs requirement =
                sprintf
                    "%s cannot be replaced in place (%s); dropping and re-creating it needs %s"
                    (identity deployed)
                    reason
                    requirement

            match
                DropSafety.removal
                    policy
                    deployed.Name
                    [ DesiredStateLoaded(needs "a desired state that loaded completely")
                      DropsEnabled(needs "--allow-drops")
                      dependentsGuard inputs deployed ]
                    (DropRoutine(deployed.Name, deployed.ArgumentTypes))
            with
            | Ok drop -> [ Ok drop; Ok(CreateRoutine declared.Name) ]
            | Error refused -> [ Error refused ]
