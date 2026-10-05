namespace Strata.Application.SchemaDiff

open System
open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Semantic.AnalysisScope
open Strata.Analysis.DialectPort
open Strata.Analysis.ProposedChange

module Objects =

    /// Views and routines, compared by PRESENCE only.
    ///
    /// Their definitions are deliberately not compared. A declared view's
    /// definition text is not recoverable from the parse tree, and PostgreSQL
    /// rewrites what it stores — `SELECT 1 AS x` comes back schema-qualified
    /// and reformatted — so even a perfect deparse would differ from
    /// `pg_get_viewdef` on a view nobody changed. A routine body has the same
    /// problem. Comparing text would report churn on every run.
    ///
    /// A declared view also carries an EMPTY column list, which means "not
    /// knowable from a file", not "no columns". Nothing here may read it as a
    /// fact, which is why only presence is compared.
    ///
    /// Presence alone is still worth having: before this, a view in the
    /// database was suppressed as not-modelled and a view in a file failed to
    /// load, so a project could not express that a view should exist at all.
    let compare (policy: RemovalPolicy) (inputs: Inputs) : FamilyDiff =
        // Declared view definitions as the SERVER renders them, keyed by
        // display name. Produced by executing the declared DDL in a
        // rolled-back transaction, which is the only way to compare a view
        // faithfully. A view missing from this list could not be normalised —
        // no privilege, a read-only target, DDL the server rejected — and is
        // disclosed rather than assumed equal.
        let normalisedViews = inputs.NormalisedViews
        let desired = inputs.Desired.Objects
        let actual = inputs.Actual.Objects
        // Routines are identified by name AND argument types: PostgreSQL
        // allows overloads, so f(int) and f(text) are different objects and
        // matching on name alone would read one as a redefinition of the other.
        let identity (o: SchemaObject) =
            match o with
            | TableObject t -> "table:" + QualifiedName.display t.Name
            | ViewObject v -> "view:" + QualifiedName.display v.Name
            | RoutineObject r ->
                sprintf "routine:%s(%s)" (QualifiedName.display r.Name) (String.concat "," r.ArgumentTypes)
            | SequenceObject s -> "sequence:" + QualifiedName.display s.Name
            | EnumObject e -> "enum:" + QualifiedName.display e.Name
            | DomainObject d -> "domain:" + QualifiedName.display d.Name

        let nonTable (objects: SchemaObject list) =
            objects
            |> List.filter (fun o ->
                match o with
                // Sequences are excluded here and compared on their own:
                // this path compares views and routines by PRESENCE alone,
                // and a sequence has a start, an increment and bounds that a
                // presence check would pass over in silence.
                | TableObject _
                | SequenceObject _
                // Excluded for the same reason a sequence is: this path
                // compares by PRESENCE, and an enum's VALUES — or a domain's
                // base type, default and checks — are most of what it is.
                // `Enums` and `Domains` compare them.
                | EnumObject _
                | DomainObject _ -> false
                | ViewObject _
                | RoutineObject _ -> true)

        let desiredOther = nonTable desired
        let actualOther = nonTable actual

        let kindOf (o: SchemaObject) =
            match o with
            | ViewObject v -> if v.IsMaterialized then "materialized view" else "view"
            | RoutineObject r -> (match r.Kind with Procedure -> "procedure" | Function -> "function")
            | TableObject _ -> "table"
            // Excluded from this path by `nonTable`; the compiler still wants
            // the arm, and inventing a wrong one would be worse than saying so.
            | SequenceObject _ -> "sequence"
            | EnumObject _ -> "enum type"
            | DomainObject _ -> "domain"

        let created =
            desiredOther
            |> List.filter (fun d -> not (actualOther |> List.exists (fun a -> identity a = identity d)))
            |> List.map (fun d ->
                match d with
                | ViewObject v -> Ok(CreateView v.Name)
                | RoutineObject r -> Ok(CreateRoutine r.Name)
                | TableObject t -> Ok(CreateTable t.Name)
                | SequenceObject sq -> Ok(CreateSequence sq.Name)
                | EnumObject e -> Ok(CreateEnumType e.Name)
                | DomainObject d -> Ok(CreateDomainType d.Name))

        let removed =
            actualOther
            |> List.filter (fun a -> not (desiredOther |> List.exists (fun d -> identity d = identity a)))
            |> List.map (fun a ->
                let name = SchemaObject.name a

                DropSafety.objectRemoval
                    policy
                    name
                    (SchemaObject.scope a)
                    { OutsideManaged =
                        sprintf "%s exists in the database and not in desired state, but its schema is not managed" (kindOf a)
                      ExtensionOwned = sprintf "%s is owned by an extension" (kindOf a)
                      Incomplete =
                        sprintf "%s is absent from desired state, but desired state did not load completely" (kindOf a)
                      DropsNotEnabled = sprintf "%s would be dropped; pass --allow-drops to propose removals" (kindOf a) }
                    (UnclassifiedChange(
                        sprintf
                            "%s %s exists in the database and not in desired state"
                            (kindOf a)
                            (QualifiedName.display name)
                    )))

        let onBothSides =
            desiredOther
            |> List.choose (fun d ->
                actualOther
                |> List.tryFind (fun a -> identity a = identity d)
                |> Option.map (fun a -> d, a))

        // A view whose declared DDL the server normalised can be compared
        // exactly: both sides now carry PostgreSQL's own rendering.
        let redefinitions =
            onBothSides
            |> List.choose (fun (d, a) ->
                match d, a with
                | ViewObject dv, ViewObject av when not dv.IsMaterialized ->
                    normalisedViews
                    |> List.tryPick (fun (name, definition) ->
                        if name = QualifiedName.display dv.Name then Some definition else None)
                    |> Option.bind (fun declaredDefinition ->
                        if declaredDefinition.Trim() <> av.Definition.Trim() then
                            Some(Ok(ReplaceView dv.Name))
                        else
                            None)
                // A routine body needs no shadow: PostgreSQL stores a classic
                // `AS $$...$$` body verbatim in prosrc, so the declared text
                // and the deployed text compare directly. A body the server
                // holds only as a parse tree (SQL-standard BEGIN ATOMIC) or as
                // a symbol name (C) yields None on one side and is disclosed.
                | RoutineObject dr, RoutineObject ar ->
                    match dr.Body, ar.Body with
                    | Some declared, Some deployed when declared.Trim() <> deployed.Trim() ->
                        Some(Ok(ReplaceRoutine dr.Name))
                    | _ -> None

                | _ -> None)

        // Everything on both sides that could NOT be compared. A view that
        // normalised and matched produces nothing here — silence is correct
        // once the comparison actually happened.
        let notCompared =
            onBothSides
            |> List.choose (fun (d, _) ->
                let comparedExactly =
                    match d with
                    | ViewObject dv when not dv.IsMaterialized ->
                        normalisedViews |> List.exists (fun (name, _) -> name = QualifiedName.display dv.Name)
                    | RoutineObject dr ->
                        // Compared only when BOTH sides hold body text.
                        dr.Body.IsSome
                        && actualOther
                           |> List.exists (fun a ->
                               match a with
                               | RoutineObject ar -> identity a = identity d && ar.Body.IsSome
                               | _ -> false)
                    | _ -> false

                if comparedExactly then None
                else
                    Some
                        { Object = SchemaObject.name d
                          Reason = NotCompared
                          Detail =
                            sprintf
                                "%s exists on both sides; its definition was NOT compared, so the bodies may differ"
                                (kindOf d) })

        { Differences = created @ removed @ redefinitions; Disclosures = notCompared }
